namespace DaServer.Persistence;

// An actor that changes node takes its record along (ADR-072): the previous owner sends its [Persist] and [Handoff]
// components, the version it last wrote and that write's hash to the new owner, on the link its forwarded mail takes
// afterwards, so the record arrives first. The new owner restores it instead of reading the store, and writes it when
// its checkpoint finds it changed. A new owner that gets mail first asks the previous owner and waits. Whatever does
// not work out (refused, no answer, node gone) falls back to the store: the previous owner writes, the new one reads.
// While views converge and keys move on, nothing is answered too early: a node owning a key in its own view, or one that
// took it from a live node moments ago, defers a request for its record; a record whose key moved on goes on once.

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
        var node = world.Shard.Node;
        var owner = node.View.HandlerTypeOwner(actor.HandlerType.Id, actor.Key);
        return owner != null && owner.NodeId != node.Id ? owner.NodeId : null;
    }

    /// <summary>
    /// Who may still hold a key this node loads, while keys move after view changes (within the handoff timeout): its
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
        if (recent.Length == 0 || Platform.Milliseconds - recent[0].ReplacedMs > TimeoutMs(node)) return null;
        var view = node.View;
        var other = view.HandlerTypeOwnerWithout(handlerTypeId, key, node.Id);
        return other != null && view.TryGetMember(node.Id, out var self) && other.UpSince < self.UpSince &&
               view.TryGetMember(other.NodeId, out var member) && member.Status != MemberStatus.Dead ? member.NodeId : null;
    }

    /// <summary>
    /// The latest live owner other than this node in the views replaced within the handoff timeout, before this node owned
    /// the key (<paramref name="ownsNow"/>: it owns it now; otherwise, a view where it did): the node that may hold the
    /// key's actor or still send its record here. Null when there is none.
    /// </summary>
    private static ulong? HeldBefore(Node node, uint handlerTypeId, long key, bool ownsNow)
    {
        long now = Platform.Milliseconds, window = TimeoutMs(node);
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
    /// Send the leaving actor's record to <paramref name="to"/> and take the state off this copy (the record's sections
    /// returned, to restore them if the handoff falls back); null when that node is gone.
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
    public static void Gone(World world, Actor actor, ulong by) => Gone(world.Shard.Node, actor.HandlerType.Id, actor.Key, by);

    /// <summary>Tell <paramref name="by"/> that this node holds no record of the key.</summary>
    public static void Gone(Node node, uint handlerTypeId, long key, ulong by) => Notify(node, by, None, handlerTypeId, key, 0);

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
            var node = world.Shard.Node;
            bool loaded = world.GetSingleton<ActorDirectory>().Actors.TryGetValue(new ActorAddress(handlerType.Id, header.Key), out var entity);
            if (op == Record)
            {
                // A record this node cannot take (the key moved on meanwhile) goes on once: to a node that asked for it, or
                // to the key's owner in this node's view. That node answers its sender.
                if (header.Hops == 0 && !loaded && !ShardDispatch.Owns(world, handlerType.Id, header.Key) &&
                    !SendersOwn(world, handlerType.Id, header.Key, from) && PassOn(world, handlerType.Id, header.Key, payload, from))
                    return true;
                Notify(node, from, Answer, handlerType.Id, header.Key, Restore(world, handlerType, header.Key, payload, loaded, entity, from) ? (byte)1 : (byte)0);
            }
            else if (op == Request)
            {
                if (!loaded)
                {
                    var wait = MustWait(world, handlerType.Id, header.Key, from);
                    if (wait == null) Notify(node, from, None, handlerType.Id, header.Key, 0);
                    else
                    {
                        Defer(world, handlerType.Id, header.Key, from);
                        // Ask the node it took the key from: its record comes (and goes on), or its "none" answers the asker.
                        if (wait != node.Id) Notify(node, wait.Value, Request, handlerType.Id, header.Key, 0);
                    }
                }
                else if (!entity.Has<HandoffWanted>()) entity.Add<HandoffWanted>() = new HandoffWanted(from); // told when it goes
            }
            else if (op == None)
            {
                if (loaded && entity.Has<AwaitingHandoff>())
                {
                    entity.Remove<AwaitingHandoff>();
                    ShardDispatch.BeginLoad(world, entity, entity.Get<ActorRef>().Actor);
                }
                else if (!loaded && !ShardDispatch.Owns(world, handlerType.Id, header.Key)) PassOn(world, handlerType.Id, header.Key, default);
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
            Log.HandoffUnreadable(world.Logger, e, actor);
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

    private static void Defer(World world, uint handlerTypeId, long key, ulong by)
    {
        var waiting = world.GetSingleton<HandoffAsks>().Waiting;
        long now = world.GetSingleton<ShardActivity>().NowMs;
        if (waiting.Count >= HandoffAsks.Max)
            for (int i = waiting.Count - 1; i >= 0; i--)
                if (waiting[i].UntilMs < now)
                {
                    waiting[i] = waiting[^1];
                    waiting.RemoveAt(waiting.Count - 1);
                }

        // Past the bound it goes unanswered: its asker asks again.
        if (waiting.Count < HandoffAsks.Max) waiting.Add(new HandoffAsk(handlerTypeId, key, by, now + TimeoutMs(world.Shard.Node)));
    }

    /// <summary>
    /// The record of a key this node cannot take goes to the first node that asked for it here, else to the key's owner
    /// in this node's view (not back to <paramref name="from"/>); without a record (the previous holder answered "none")
    /// every asker is told "none". False when the record went nowhere.
    /// </summary>
    private static bool PassOn(World world, uint handlerTypeId, long key, ReadOnlySpan<byte> record, ulong from = 0)
    {
        var node = world.Shard.Node;
        var waiting = world.GetSingleton<HandoffAsks>().Waiting;
        ulong to = 0;
        for (int i = waiting.Count - 1; i >= 0 && (record.IsEmpty || to == 0); i--)
        {
            var ask = waiting[i];
            if (ask.HandlerTypeId != handlerTypeId || ask.Key != key) continue;
            waiting[i] = waiting[^1];
            waiting.RemoveAt(waiting.Count - 1);
            if (record.IsEmpty) Gone(node, handlerTypeId, key, ask.By);
            else to = ask.By; // one record, one asker: the others ask again and find it there
        }

        if (record.IsEmpty) return false;
        if (to == 0 && node.View.HandlerTypeOwner(handlerTypeId, key) is { } owner && owner.NodeId != node.Id && owner.NodeId != from) to = owner.NodeId;
        if (to == 0) return false;
        var w = FrameWriter.Rent(FrameHeader.Size + record.Length);
        w.WriteRaw(record);
        w.SetHeader(FrameKind.Message, handlerTypeId, Record, key, hops: 1);
        return node.SendToNode(to, w);
    }

    private static bool SendersOwn(World world, uint handlerTypeId, long key, ulong from) =>
        !world.GetSingleton<ShardActivity>().Stopping && world.Shard.Node.View.HandlerTypeOwner(handlerTypeId, key)?.NodeId == from;

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
