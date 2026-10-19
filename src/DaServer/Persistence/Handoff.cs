namespace DaServer.Persistence;

// An actor that changes node takes its record along (ADR-072): the previous owner sends its [Persist] and [Handoff]
// components, the version it last wrote and that write's hash to the new owner, on the link its forwarded mail takes
// afterwards, so the record arrives first. The new owner restores it instead of reading the store, and writes it when
// its checkpoint finds it changed. A new owner that gets mail first asks the previous owner and waits. Whatever does
// not work out (refused, no answer, node gone) falls back to the store: the previous owner writes, the new one reads.
// While views converge and keys move on, nothing is answered too early: a node owning a key in its own view, or one that
// took it from a live node moments ago, defers a request for its record; a record whose key moved on goes on once.

/// <summary>The handoff frames: messages to an actor's handler type that no handler handles, read by the runtime.</summary>
internal static partial class Handoff
{
    private struct RecordOp;
    private struct RequestOp;
    private struct NoneOp;
    private struct AnswerOp;
    private struct ResentOp;
    private struct MovedOnOp;

    private static readonly uint Record = TypeId<RecordOp>.Value;   // [sender][version][hash][handoff id][critical][attempt][sections]
    private static readonly uint Resent = TypeId<ResentOp>.Value;   // a Record again (its answer was lost or it was)
    private static readonly uint MovedOn = TypeId<MovedOnOp>.Value; // [sender][node]: the record went to that node, ask it
    private static readonly uint Request = TypeId<RequestOp>.Value; // [sender][0]: send me the record if you hold it
    private static readonly uint None = TypeId<NoneOp>.Value;       // [sender][0]: I do not hold it
    private static readonly uint Answer = TypeId<AnswerOp>.Value;   // [sender][1 accepted, 0 refused]
    private const int CriticalAt = 2 * sizeof(ulong) + sizeof(long) + sizeof(ulong); // 1: a critical save was pending
    private const int AttemptAt = CriticalAt + 1; // the hash of a write whose answer was lost (Stored.Attempt), or 0
    private const int RecordHeader = AttemptAt + sizeof(ulong);

    /// <summary>Where a leaving actor's record goes: the key's owner when that is another node and there is a record to move.</summary>
    public static ulong? TargetOf(World world, Actor actor)
    {
        if (!world.GetSingleton<ShardStore>().HandsOff) return null;
        var node = world.Shard.Node;
        var owner = node.View.HandlerTypeOwner(actor.HandlerType.Id, actor.Key);
        return owner != null && owner.NodeId != node.Id ? owner.NodeId : null;
    }

    /// <summary>
    /// Who may still hold a key this node loads, while keys move after view changes (within the move window, <see cref="MoveWindowMs"/>): its
    /// latest owner other than this node in the recent views, or, when there is none (it just joined, its previous views
    /// lacked the others), the node owning it without this one if that node is up longer (one that joined after this
    /// node never held it). Null otherwise: nothing is on its way, so a load waits for no one.
    /// </summary>
    public static ulong? PreviousOwner(World world, uint handlerTypeId, long key)
    {
        if (!world.GetSingleton<ShardStore>().HandsOff) return null;
        var node = world.Shard.Node;
        if (HeldBefore(node, handlerTypeId, key, ownsNow: true) is { } held) return held;
        var recent = node.RecentViews;
        if (recent.Length == 0 || Platform.Milliseconds - recent[0].ReplacedMs > MoveWindowMs(node.Options)) return null;
        var view = node.View;
        var other = view.HandlerTypeOwnerWithout(handlerTypeId, key, node.Id);
        return other != null && view.TryGetMember(node.Id, out var self) && other.UpSince < self.UpSince &&
               view.TryGetMember(other.NodeId, out var member) && member.Status != MemberStatus.Dead ? member.NodeId : null;
    }

    /// <summary>
    /// The latest live owner other than this node in the views replaced within the move window, before this node owned
    /// the key (<paramref name="ownsNow"/>: it owns it now; otherwise, a view where it did): the node that may hold the
    /// key's actor or still send its record here. Null when there is none.
    /// </summary>
    private static ulong? HeldBefore(Node node, uint handlerTypeId, long key, bool ownsNow)
    {
        long now = Platform.Milliseconds, window = MoveWindowMs(node.Options);
        bool mine = ownsNow;
        foreach (var recent in node.RecentViews)
        {
            if (now - recent.ReplacedMs > window) break;
            var owner = recent.View.HandlerTypeOwner(handlerTypeId, key);
            if (owner == null) continue;
            if (owner.NodeId == node.Id) mine = true;
            else if (mine)
                return node.View.TryGetMember(owner.NodeId, out var member) && member.Status != MemberStatus.Dead ? member.NodeId : null;
        }

        return null;
    }

    /// <summary>
    /// The node this one, holding no copy of the key, must hear from before answering "none": itself when it owns the key
    /// in its own view (mail routed by it would load a second copy beside the asker's), or the live node it took the key
    /// from shortly before (that node's record may still come here, and goes on to the asker then). Null: answer now.
    /// </summary>
    internal static ulong? MustWait(World world, uint handlerTypeId, long key, ulong by)
    {
        var node = world.Shard.Node;
        if (ShardDispatch.Owns(world, handlerTypeId, key)) return node.Id;
        return world.GetSingleton<ShardStore>().HandsOff && HeldBefore(node, handlerTypeId, key, ownsNow: false) is { } held && held != by ? held : null;
    }

    /// <summary>Ask <paramref name="from"/> for the record of a new actor; it loads from the store at once when that node is gone.</summary>
    public static void Await(World world, Entity entity, ulong from)
    {
        var node = world.Shard.Node;
        var actor = entity.Get<ActorRef>().Actor;
        if (Notify(node, from, Request, actor.HandlerType.Id, actor.Key, 0))
            entity.Add<AwaitingHandoff>() = new AwaitingHandoff(from, world.GetSingleton<ShardActivity>().NowMs + TimeoutMs(node));
        else ShardDispatch.BeginLoad(world, entity, actor);
    }

    /// <summary>Ask <paramref name="from"/> again for the record of <paramref name="actor"/>; false when that node is gone.</summary>
    public static bool AskAgain(Node node, ulong from, Actor actor) => Notify(node, from, Request, actor.HandlerType.Id, actor.Key, 0);

    /// <summary>
    /// Send the leaving actor's record to <paramref name="to"/> and take the state off this copy (the record returned, to
    /// send again or to restore if the handoff falls back); null when that node is gone or the state does not serialize
    /// (the handoff falls back: the store, which cannot write it either, keeps the last record).
    /// </summary>
    public static byte[]? Send(World world, Entity entity, Actor actor, ulong to)
    {
        var node = world.Shard.Node;
        var stored = entity.Has<Stored>() ? entity.Get<Stored>() : default;
        var record = FrameWriter.Rent(1024);
        var head = record.GetSpan(RecordHeader);
        BinaryPrimitives.WriteUInt64LittleEndian(head, node.Id);
        BinaryPrimitives.WriteInt64LittleEndian(head[sizeof(ulong)..], stored.Version);
        BinaryPrimitives.WriteUInt64LittleEndian(head[(sizeof(ulong) + sizeof(long))..], stored.Hash);
        BinaryPrimitives.WriteUInt64LittleEndian(head[(2 * sizeof(ulong) + sizeof(long))..], (ulong)Random.Shared.NextInt64() | 1); // this handoff
        head[CriticalAt] = entity.Has<CriticalPending>() ? (byte)1 : (byte)0; // its write failed: the new owner writes it at once
        BinaryPrimitives.WriteUInt64LittleEndian(head[AttemptAt..], stored.Attempt); // and that write may have landed
        record.Advance(RecordHeader);
        var moved = world.GetSingleton<ShardStore>().Moved;
        try
        {
            Records.WriteSections(entity, moved, record);
        }
        catch (Exception e)
        {
            record.Dispose();
            Log.RecordUnwritable(node.Logger, e, actor, 0); // once per move
            return null;
        }

        record.SetHeader(FrameKind.Message, actor.HandlerType.Id, Record, actor.Key);
        var payload = FrameHeader.PayloadOf(FrameHeader.Read(record.Frame), record.Frame).ToArray(); // cold: one per move
        if (node.LoseRecords > 0 && Interlocked.Decrement(ref node.LoseRecords) >= 0) record.Dispose(); // tests: lost on the way
        else if (!node.SendToNode(to, record)) return null;
        foreach (var type in moved) type.Remove(entity);
        actor.ReleaseState(entity);
        return payload;
    }

    /// <summary>
    /// Send a record (<see cref="Send"/>'s) again, as a resend: only a copy waiting for it takes it, and the node that
    /// took (or passed on) the first send answers for it; false when that node is gone.
    /// </summary>
    public static bool Resend(Node node, Actor actor, ulong to, byte[] record) =>
        SendRecord(node, to, Resent, actor.HandlerType.Id, actor.Key, record, hops: 0);

    private static bool SendRecord(Node node, ulong to, uint op, uint handlerTypeId, long key, ReadOnlySpan<byte> record, byte hops)
    {
        var w = FrameWriter.Rent(FrameHeader.Size + record.Length);
        w.WriteRaw(record);
        w.SetHeader(FrameKind.Message, handlerTypeId, op, key, hops: hops);
        return node.SendToNode(to, w);
    }

    /// <summary>The handoff was refused or not answered: the copy takes its state back, then the store keeps it.</summary>
    public static void Reclaim(World world, Entity entity, byte[] record)
    {
        var actor = entity.Get<ActorRef>().Actor;
        try
        {
            Records.Read(entity, record.AsSpan(RecordHeader), world.GetSingleton<ShardStore>().ById);
        }
        catch (Exception e)
        {
            // Its own record, part read: writing that would replace the store's full record with a part of it.
            Log.RestoreFailed(world.Logger, e, actor);
            entity.Add<Removing>() = new Removing(RpcStatus.Error, "Taking the actor's record back failed: " + e.Message);
            return;
        }

        try
        {
            actor.InitializeEntity(entity); // the states not moved start new: user initializers
        }
        catch (Exception e)
        {
            Log.ActorFailed(world.Logger, e, "Reclaim", actor); // the record's states are all here: they are written
        }

        Fallback(entity);
    }

    /// <summary>The handoff went nowhere: the store keeps the record (or, without one, the actor just goes).</summary>
    public static void Fallback(Entity entity)
    {
        if (entity.Has<Stored>()) entity.Add<FinalSave>();
        else entity.Add<Removing>() = new Removing(RpcStatus.Ok, null);
    }

    /// <summary>Tell the node that asked for the record of a removed actor that it was not handed to it.</summary>
    public static void Gone(World world, Actor actor, ulong by) => Gone(world.Shard.Node, actor.HandlerType.Id, actor.Key, by);

    /// <summary>Tell <paramref name="by"/>, which asked for the record of a removed actor, that node <paramref name="to"/> took it.</summary>
    public static void Moved(World world, Actor actor, ulong by, ulong to) => Moved(world.Shard.Node, actor.HandlerType.Id, actor.Key, by, to);

    private static void Moved(Node node, uint handlerTypeId, long key, ulong by, ulong to) =>
        Notify(node, by, MovedOn, handlerTypeId, key, to, sizeof(ulong));

    /// <summary>A copy whose OnLoad failed after a restore: its state back to the record as it came (written next).</summary>
    public static void RestoreAsReceived(World world, Entity entity, byte[] payload) =>
        Records.Read(entity, payload.AsSpan(RecordHeader), world.GetSingleton<ShardStore>().ById);

    /// <summary>Tell <paramref name="by"/> that this node holds no record of the key.</summary>
    public static void Gone(Node node, uint handlerTypeId, long key, ulong by) => Notify(node, by, None, handlerTypeId, key, 0);

    public static long TimeoutMs(Node node) => (long)node.Options.CallTimeout.TotalMilliseconds;

    /// <summary>
    /// How long after a view change a node assumes the key's previous owner may still hold its actor: a turn in flight
    /// (a call timeout), its write in flight (a store timeout), and as much again for the record and the answers. A copy
    /// held longer than that (a turn running for minutes) meets a new owner that loaded from the store: versions decide,
    /// and the copy's changes since its last write are lost (ADR-072).
    /// </summary>
    public static long MoveWindowMs(NodeOptions options) =>
        2 * (long)(options.CallTimeout.TotalMilliseconds + options.StoreTimeout.TotalMilliseconds);

    /// <summary>A handoff frame [sender][value, <paramref name="size"/> bytes little-endian] to node <paramref name="to"/>; false when it is gone.</summary>
    private static bool Notify(Node node, ulong to, uint op, uint handlerTypeId, long key, ulong value, int size)
    {
        var w = FrameWriter.Rent(FrameHeader.Size + sizeof(ulong) + size);
        var span = w.GetSpan(2 * sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, node.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(span[sizeof(ulong)..], value); // its low bytes first
        w.Advance(sizeof(ulong) + size);
        w.SetHeader(FrameKind.Message, handlerTypeId, op, key);
        return node.SendToNode(to, w);
    }
}
