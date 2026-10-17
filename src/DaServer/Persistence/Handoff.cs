namespace DaServer.Persistence;

// An actor that changes node takes its record along (ADR-072): the previous owner sends its [Persist] and [Handoff]
// components, the version it last wrote and that write's hash to the new owner, on the link its forwarded mail takes
// afterwards, so the record arrives first. The new owner restores it instead of reading the store, and writes it when
// its checkpoint finds it changed. A new owner that gets mail first asks the previous owner and waits. Whatever does
// not work out (refused, no answer, node gone) falls back to the store: the previous owner writes, the new one reads.

/// <summary>The actor leaves for node <see cref="To"/>, which owns it now: its record goes there once no write is in flight.</summary>
internal readonly record struct HandOff(ulong To) : IComponent;

/// <summary>
/// The record went to <see cref="To"/>: the actor is removed (its mail forwarded) once it accepted, or saved to the
/// store when it refused or did not answer by <see cref="UntilMs"/>.
/// The copy holds no state meanwhile (no system sees it): <see cref="Record"/> restores it for that save.
/// </summary>
internal readonly record struct HandingOff(ulong To, long UntilMs, byte[] Record) : IComponent;

/// <summary>
/// The actor moved here from <see cref="From"/>, which may still hold it: its mail waits for the record, or for word that
/// there is none, until <see cref="UntilMs"/>; then it loads from the store.
/// </summary>
internal readonly record struct AwaitingHandoff(ulong From, long UntilMs) : IComponent;

/// <summary>Node <see cref="By"/> waits for this actor's record: told when the actor goes without handing it to it.</summary>
internal readonly record struct HandoffWanted(ulong By) : IComponent;

/// <summary>The handoff frames: messages to an actor's handler type that no handler handles, read by the runtime.</summary>
internal static class Handoff
{
    private struct RecordOp;
    private struct RequestOp;
    private struct NoneOp;
    private struct AnswerOp;

    private static readonly uint Record = TypeId<RecordOp>.Value;   // [sender][version][hash][record sections]
    private static readonly uint Request = TypeId<RequestOp>.Value; // [sender][0]: send me the record if you hold it
    private static readonly uint None = TypeId<NoneOp>.Value;       // [sender][0]: I do not hold it
    private static readonly uint Answer = TypeId<AnswerOp>.Value;   // [sender][1 accepted, 0 refused]
    private const int RecordHeader = sizeof(ulong) + sizeof(long) + sizeof(ulong);

    /// <summary>Where a leaving actor's record goes: the key's owner when that is another node and there is a record to move.</summary>
    public static ulong? TargetOf(World world, Actor actor)
    {
        if (!world.GetSingleton<ShardStore>().HandsOff) return null;
        var node = world.Shard().Node;
        var owner = node.View.HandlerTypeOwner(actor.HandlerType.Id, actor.Key);
        return owner != null && owner.NodeId != node.Id ? owner.NodeId : null;
    }

    /// <summary>
    /// Who may still hold a key this node loads, while keys move after a view change (within the handoff timeout): its
    /// owner in the previous view, or, when that was this node (it just joined, its previous view lacked the others), the
    /// node owning it without this one. Null otherwise: nothing is on its way, so a load waits for no one.
    /// </summary>
    public static ulong? PreviousOwner(World world, uint handlerTypeId, long key)
    {
        if (!world.GetSingleton<ShardStore>().HandsOff) return null;
        var node = world.Shard().Node;
        if (node.SinceViewChangeMs > TimeoutMs(node)) return null;
        var view = node.View;
        var previous = node.PreviousView?.HandlerTypeOwner(handlerTypeId, key);
        if (previous == null || previous.NodeId == node.Id) previous = view.HandlerTypeOwnerWithout(handlerTypeId, key, node.Id);
        if (previous == null || previous.NodeId == node.Id) return null;
        return view.TryGetMember(previous.NodeId, out var member) && member.Status != MemberStatus.Dead ? member.NodeId : null;
    }

    /// <summary>Ask <paramref name="from"/> for the record of a new actor; it loads from the store at once when that node is gone.</summary>
    public static void Await(World world, Entity entity, ulong from)
    {
        var node = world.Shard().Node;
        var actor = entity.Get<ActorRef>().Actor;
        if (Notify(node, from, Request, actor.HandlerType.Id, actor.Key, 0))
            entity.Add<AwaitingHandoff>() = new AwaitingHandoff(from, world.GetSingleton<ShardActivity>().NowMs + TimeoutMs(node));
        else ShardDispatch.BeginLoad(world, entity, actor);
    }

    /// <summary>Ask <paramref name="from"/> again for the record of <paramref name="actor"/>; false when that node is gone.</summary>
    public static bool AskAgain(Node node, ulong from, Actor actor) => Notify(node, from, Request, actor.HandlerType.Id, actor.Key, 0);

    /// <summary>
    /// Send the leaving actor's record to <paramref name="to"/> and take the state off this copy (the record's sections
    /// returned, to restore them if the handoff falls back); null when that node is gone.
    /// </summary>
    public static byte[]? Send(World world, Entity entity, Actor actor, ulong to)
    {
        var node = world.Shard().Node;
        var stored = entity.Has<Stored>() ? entity.Get<Stored>() : default;
        var record = FrameWriter.Rent(1024);
        var head = record.GetSpan(RecordHeader);
        BinaryPrimitives.WriteUInt64LittleEndian(head, node.Id);
        BinaryPrimitives.WriteInt64LittleEndian(head[sizeof(ulong)..], stored.Version);
        BinaryPrimitives.WriteUInt64LittleEndian(head[(sizeof(ulong) + sizeof(long))..], stored.Hash);
        record.Advance(RecordHeader);
        var moved = world.GetSingleton<ShardStore>().Moved;
        Records.WriteSections(entity, moved, record);
        record.SetHeader(FrameKind.Message, actor.HandlerType.Id, Record, actor.Key);
        var sections = FrameHeader.PayloadOf(FrameHeader.Read(record.Frame), record.Frame)[RecordHeader..].ToArray(); // cold: one per move
        if (!node.SendToNode(to, record)) return null;
        foreach (var type in moved) type.Remove(entity);
        actor.ReleaseState(entity);
        return sections;
    }

    /// <summary>The handoff was refused or not answered: the copy takes its state back, then the store keeps it.</summary>
    public static void Reclaim(World world, Entity entity, byte[] record)
    {
        Records.Read(entity, record, world.GetSingleton<ShardStore>().ById);
        entity.Get<ActorRef>().Actor.InitializeEntity(entity);
        Fallback(entity);
    }

    /// <summary>The handoff went nowhere: the store keeps the record (or, without one, the actor just goes).</summary>
    public static void Fallback(Entity entity)
    {
        if (entity.Has<Stored>()) entity.Add<FinalSave>();
        else entity.Add<Removing>() = new Removing(RpcStatus.Ok, null);
    }

    /// <summary>Tell the node that asked for the record of a removed actor that it was not handed to it.</summary>
    public static void Gone(World world, Actor actor, ulong by) =>
        Notify(world.Shard().Node, by, None, actor.HandlerType.Id, actor.Key, 0);

    public static long TimeoutMs(Node node) => (long)node.Options.CallTimeout.TotalMilliseconds;

    /// <summary>
    /// A handoff frame reached a shard that stopped (its node leaves): it holds no actor any more, so a request for a
    /// record is answered "none" and a record is refused (its sender falls back at once instead of waiting for the
    /// timeout); answers are dropped. False when <paramref name="frame"/> is not a handoff frame. Any thread.
    /// </summary>
    public static bool Bounce(Node node, FrameWriter frame)
    {
        var header = FrameHeader.Read(frame.Frame);
        uint op = header.MessageId;
        if (header.Kind != FrameKind.Message || op != Record && op != Request && op != None && op != Answer) return false;
        ulong from = BinaryPrimitives.ReadUInt64LittleEndian(FrameHeader.PayloadOf(in header, frame.Frame));
        if (op == Request) Notify(node, from, None, header.Target, header.Key, 0);
        else if (op == Record) Notify(node, from, Answer, header.Target, header.Key, 0);
        frame.Dispose();
        return true;
    }

    /// <summary>Handle a handoff frame (shard thread); false when <paramref name="header"/> is not one. Disposes <paramref name="frame"/> when it is.</summary>
    public static bool TryReceive(World world, HandlerTypeInfo handlerType, FrameWriter frame, in FrameHeader header)
    {
        uint op = header.MessageId;
        if (op != Record && op != Request && op != None && op != Answer) return false;
        try
        {
            var payload = FrameHeader.PayloadOf(in header, frame.Frame);
            ulong from = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            var node = world.Shard().Node;
            bool loaded = world.GetSingleton<ActorDirectory>().Actors.TryGetValue(new ActorAddress(handlerType.Id, header.Key), out var entity);
            if (op == Record)
                Notify(node, from, Answer, handlerType.Id, header.Key, Restore(world, handlerType, header.Key, payload, loaded, entity, from) ? (byte)1 : (byte)0);
            else if (op == Request)
            {
                if (!loaded) Notify(node, from, None, handlerType.Id, header.Key, 0);
                else if (!entity.Has<HandoffWanted>()) entity.Add<HandoffWanted>() = new HandoffWanted(from); // told when it goes
            }
            else if (op == None)
            {
                if (loaded && entity.Has<AwaitingHandoff>())
                {
                    entity.Remove<AwaitingHandoff>();
                    ShardDispatch.BeginLoad(world, entity, entity.Get<ActorRef>().Actor);
                }
            }
            else if (loaded && entity.TryGet<HandingOff>(out var handingOff))
            {
                entity.Remove<HandingOff>();
                if (payload[sizeof(ulong)] == 1) entity.Add<Removing>() = new Removing(RpcStatus.Ok, null); // its mail follows the record
                else Reclaim(world, entity, handingOff.Record);
            }
        }
        finally
        {
            frame.Dispose();
        }

        return true;
    }

    /// <summary>
    /// Take a record: for an actor waiting for it, or a new one this shard owns, or one its sender owns in this node's
    /// view (the sender gives its own key away: its view is ahead of this one, it is leaving and this node has not
    /// heard yet). Refused for an actor already running here (it loaded from the store meanwhile; the sender writes its
    /// record, versions settle who wins) and while this shard stops.
    /// </summary>
    private static bool Restore(World world, HandlerTypeInfo handlerType, long key, ReadOnlySpan<byte> payload, bool loaded, Entity entity,
        ulong from)
    {
        if (loaded ? !entity.Has<AwaitingHandoff>() : !ShardDispatch.Owns(world, handlerType.Id, key) && !SendersOwn(world, handlerType.Id, key, from))
            return false;
        if (loaded) entity.Remove<AwaitingHandoff>();
        else entity = ShardDispatch.Adopt(world, handlerType, key);
        var actor = entity.Get<ActorRef>().Actor;
        var store = world.GetSingleton<ShardStore>();
        try
        {
            Records.Read(entity, payload[RecordHeader..], store.ById);
            actor.InitializeEntity(entity);
        }
        catch (Exception e)
        {
            Log.HandoffUnreadable(world.Logger(), e, actor);
            if (loaded) ShardDispatch.BeginLoad(world, entity, actor);
            else entity.Add<Removing>() = new Removing(RpcStatus.Ok, null); // nothing queued yet
            return false;
        }

        if (store.Enabled)
        {
            long every = (long)actor.SaveEvery.TotalMilliseconds;
            entity.Add<Stored>() = new Stored
            {
                Version = BinaryPrimitives.ReadInt64LittleEndian(payload[sizeof(ulong)..]),
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(payload[(sizeof(ulong) + sizeof(long))..]), // changes since are written
                EveryMs = every,
                DueMs = world.GetSingleton<ShardActivity>().NowMs + every,
            };
        }

        ShardDispatch.BeginLive(world, entity, actor);
        return true;
    }

    private static bool SendersOwn(World world, uint handlerTypeId, long key, ulong from) =>
        !world.GetSingleton<ShardActivity>().Stopping && world.Shard().Node.View.HandlerTypeOwner(handlerTypeId, key)?.NodeId == from;

    private static bool Notify(Node node, ulong to, uint op, uint handlerTypeId, long key, byte value)
    {
        var w = FrameWriter.Rent(FrameHeader.Size + sizeof(ulong) + 1);
        var span = w.GetSpan(sizeof(ulong) + 1);
        BinaryPrimitives.WriteUInt64LittleEndian(span, node.Id);
        span[sizeof(ulong)] = value;
        w.Advance(sizeof(ulong) + 1);
        w.SetHeader(FrameKind.Message, handlerTypeId, op, key);
        return node.SendToNode(to, w);
    }
}

/// <summary>Sends the records of actors leaving for another node (<see cref="HandOff"/>), once no write of theirs is in flight.</summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class HandoffSystem : EcsSystem
{
    public override int Priority => 6;

    // After its write in flight and a critical save asked for (the record carries the version they produce).
    [ForEach, Without<Saving>, Without<SaveNow>]
    private static void Send(Entity actor, in HandOff handOff, in ActorRef self, in ShardActivity activity, World world)
    {
        var (to, target, now) = (handOff.To, self.Actor, activity.NowMs);
        actor.Remove<HandOff>(); // the refs above are invalid from here on
        if (Handoff.Send(world, actor, target, to) is { } record)
            actor.Add<HandingOff>() = new HandingOff(to, now + Handoff.TimeoutMs(world.Shard().Node), record);
        else Handoff.Fallback(actor);
    }
}

/// <summary>Ends handoffs that got no answer: the store takes over (<see cref="ShardIdleScan"/>, a few times a second).</summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed partial class HandoffTimeoutSystem : EcsSystem
{
    [ForEach]
    private static void Waited(Entity actor, in AwaitingHandoff awaiting, in ActorRef self, in ShardActivity activity, World world)
    {
        if (activity.NowMs < awaiting.UntilMs) return;
        var (from, target, now) = (awaiting.From, self.Actor, activity.NowMs);
        var node = world.Shard().Node;
        // The previous owner may still run the actor (a long turn, a save, a slow unload): while it is alive the record
        // will come (or "none"), so ask again instead of loading a second copy from the store beside it.
        if (node.View.TryGetMember(from, out var member) && member.Status != MemberStatus.Dead && Handoff.AskAgain(node, from, target))
        {
            actor.Get<AwaitingHandoff>() = new AwaitingHandoff(from, now + Handoff.TimeoutMs(node));
            return;
        }

        actor.Remove<AwaitingHandoff>();
        ShardDispatch.BeginLoad(world, actor, target);
    }

    [ForEach]
    private static void Unanswered(Entity actor, in HandingOff handingOff, in ShardActivity activity, World world)
    {
        if (activity.NowMs < handingOff.UntilMs) return;
        var record = handingOff.Record;
        actor.Remove<HandingOff>(); // `handingOff` is invalid from here on
        Handoff.Reclaim(world, actor, record);
    }
}
