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
    private struct ResentOp;
    private struct MovedOnOp;

    private static readonly uint Record = TypeId<RecordOp>.Value;   // [sender][version][hash][handoff id][record sections]
    private static readonly uint Resent = TypeId<ResentOp>.Value;   // a Record again (its answer was lost or it was)
    private static readonly uint MovedOn = TypeId<MovedOnOp>.Value; // [sender][node]: the record went to that node, ask it
    private static readonly uint Request = TypeId<RequestOp>.Value; // [sender][0]: send me the record if you hold it
    private static readonly uint None = TypeId<NoneOp>.Value;       // [sender][0]: I do not hold it
    private static readonly uint Answer = TypeId<AnswerOp>.Value;   // [sender][1 accepted, 0 refused]
    private const int RecordHeader = sizeof(ulong) + sizeof(long) + sizeof(ulong) + sizeof(ulong) + 1;
    private const int CriticalAt = 2 * sizeof(ulong) + sizeof(long) + sizeof(ulong); // 1: a critical save was pending

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
        if (recent.Length == 0 || Platform.Milliseconds - recent[0].ReplacedMs > MoveWindowMs(node.Options)) return null;
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
    /// send again or to restore if the handoff falls back); null when that node is gone.
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
        record.Advance(RecordHeader);
        var moved = world.GetSingleton<ShardStore>().Moved;
        Records.WriteSections(entity, moved, record);
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
        Records.Read(entity, record.AsSpan(RecordHeader), world.GetSingleton<ShardStore>().ById);
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

    /// <summary>Tell <paramref name="by"/>, which asked for the record of a removed actor, that node <paramref name="to"/> took it.</summary>
    public static void Moved(World world, Actor actor, ulong by, ulong to) => Moved(world.Shard.Node, actor.HandlerType.Id, actor.Key, by, to);

    private static void Moved(Node node, uint handlerTypeId, long key, ulong by, ulong to)
    {
        var w = FrameWriter.Rent(FrameHeader.Size + 2 * sizeof(ulong));
        var span = w.GetSpan(2 * sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, node.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(span[sizeof(ulong)..], to);
        w.Advance(2 * sizeof(ulong));
        w.SetHeader(FrameKind.Message, handlerTypeId, MovedOn, key);
        node.SendToNode(by, w);
    }

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

    /// <summary>
    /// A handoff frame reached a shard that stopped (its node leaves): it holds no actor any more, so a request for a
    /// record is answered "none" and a record is refused (its sender falls back at once instead of waiting for the
    /// timeout); answers are dropped. False when <paramref name="frame"/> is not a handoff frame. Any thread.
    /// </summary>
    public static bool Bounce(Node node, FrameWriter frame)
    {
        var header = FrameHeader.Read(frame.Frame);
        uint op = header.MessageId;
        if (header.Kind != FrameKind.Message || !IsHandoff(op)) return false;
        ulong from = BinaryPrimitives.ReadUInt64LittleEndian(FrameHeader.PayloadOf(in header, frame.Frame));
        if (op == Request) Notify(node, from, None, header.Target, header.Key, 0);
        else if (op == Record) Notify(node, from, Answer, header.Target, header.Key, 0);
        // A resend goes unanswered: this node may have taken its first send (the stopped shard knew), so a refusal could
        // make its sender write a record that moved on; the sender falls back once this node has left.
        frame.Dispose();
        return true;
    }

    /// <summary>Handle a handoff frame (shard thread); false when <paramref name="header"/> is not one. Disposes <paramref name="frame"/> when it is.</summary>
    public static bool TryReceive(World world, HandlerTypeInfo handlerType, FrameWriter frame, in FrameHeader header)
    {
        uint op = header.MessageId;
        if (!IsHandoff(op)) return false;
        try
        {
            var payload = FrameHeader.PayloadOf(in header, frame.Frame);
            ulong from = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            var node = world.Shard.Node;
            bool loaded = world.GetSingleton<ActorDirectory>().Actors.TryGetValue(new ActorAddress(handlerType.Id, header.Key), out var entity);
            if (op == Record || op == Resent)
            {
                ulong id = IdOf(payload);
                ref var seen = ref world.GetSingleton<HandoffsSeen>();
                if (loaded && entity.TryGet<TakenHandoff>(out var taken) && taken.Id == id)
                {
                    Notify(node, from, Answer, handlerType.Id, header.Key, 1); // the copy running here came from it
                    return true;
                }

                if (seen.ById.TryGetValue(id, out var before))
                {
                    // This handoff was here before: taken (answered "accepted" again, wherever the actor went since), or
                    // passed on (the resend follows it there, and that node answers; gone, nobody runs it: refused).
                    if (before.PassedTo == 0) Notify(node, from, Answer, handlerType.Id, header.Key, 1);
                    else if (!SendRecord(node, before.PassedTo, Resent, handlerType.Id, header.Key, payload, hops: 1))
                        Notify(node, from, Answer, handlerType.Id, header.Key, 0);
                    return true;
                }

                if (op == Resent)
                {
                    // Never here before (as far as this shard remembers): a copy waiting for the key's record from its
                    // sender takes it, and so does this shard for a key it owns and holds no copy of, as for a first send.
                    // Refused, its sender keeps the state and the store decides; it is never passed on.
                    bool takes = loaded
                        ? entity.TryGet<AwaitingHandoff>(out var awaiting) && awaiting.From == from
                        : header.Hops == 0 && world.GetSingleton<ShardActivity>().NowMs > seen.ForgotUntilMs && ShardDispatch.Owns(world, handlerType.Id, header.Key);
                    bool again = takes && Restore(world, handlerType, header.Key, payload, loaded, entity, from);
                    if (again) Seen(world, id, 0);
                    Notify(node, from, Answer, handlerType.Id, header.Key, again ? (byte)1 : (byte)0);
                    return true;
                }

                // A record this node cannot take (the key moved on meanwhile) goes on once: to a node that asked for it, or
                // to the key's owner in this node's view. That node answers its sender.
                if (header.Hops == 0 && !loaded && !ShardDispatch.Owns(world, handlerType.Id, header.Key) &&
                    !SendersOwn(world, handlerType.Id, header.Key, from) && PassOn(world, handlerType.Id, header.Key, payload, from) is var to and not 0)
                {
                    Seen(world, id, to);
                    return true;
                }

                bool restored = Restore(world, handlerType, header.Key, payload, loaded, entity, from);
                if (restored) Seen(world, id, 0);
                Notify(node, from, Answer, handlerType.Id, header.Key, restored ? (byte)1 : (byte)0);
            }
            else if (op == Request)
            {
                if (!loaded && HandedTo(world, handlerType.Id, header.Key) is { } handed && handed != from)
                    Moved(node, handlerType.Id, header.Key, from, handed); // its record went there: ask that node
                else if (!loaded)
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
                else if (entity.TryGet<HandingOff>(out var handingOff) && handingOff.To == from)
                    Resend(node, entity.Get<ActorRef>().Actor, from, handingOff.Record); // the asker never got it
                else
                {
                    if (!entity.Has<HandoffWanted>()) entity.Add<HandoffWanted>() = new HandoffWanted(from); // told when it goes
                    // A copy this node runs for a key the asker owns in this node's view (adopted from a sender ahead of
                    // this view, which then moved back): it leaves, or it would stay until this node's next view change.
                    if (!ShardDispatch.Owns(world, handlerType.Id, header.Key) && node.View.HandlerTypeOwner(handlerType.Id, header.Key)?.NodeId == from &&
                        !entity.Has<UnloadRequested>() && !entity.Has<Unloading>() && !entity.Has<Removing>())
                        entity.Add<UnloadRequested>();
                }
            }
            else if (op == None)
            {
                if (loaded && entity.TryGet<AwaitingHandoff>(out var asked) && asked.From == from) // the node it asked, not a late one
                {
                    entity.Remove<AwaitingHandoff>();
                    ShardDispatch.BeginLoad(world, entity, entity.Get<ActorRef>().Actor);
                }
                else if (!loaded && !ShardDispatch.Owns(world, handlerType.Id, header.Key)) PassOn(world, handlerType.Id, header.Key, default);
            }
            else if (op == MovedOn)
            {
                // The node asked gave the record to another: wait for that one instead (it answers when its copy goes).
                if (loaded && entity.TryGet<AwaitingHandoff>(out var asked) && asked.From == from)
                {
                    ulong to = BinaryPrimitives.ReadUInt64LittleEndian(payload[sizeof(ulong)..]);
                    if (to == node.Id || !Notify(node, to, Request, handlerType.Id, header.Key, 0))
                    {
                        entity.Remove<AwaitingHandoff>(); // it came here after all, or that node is gone: the store
                        ShardDispatch.BeginLoad(world, entity, entity.Get<ActorRef>().Actor);
                    }
                    else entity.Get<AwaitingHandoff>() = new AwaitingHandoff(to, world.GetSingleton<ShardActivity>().NowMs + TimeoutMs(node));
                }
            }
            else if (loaded && entity.TryGet<HandingOff>(out var handingOff))
            {
                entity.Remove<HandingOff>();
                if (payload[sizeof(ulong)] == 1)
                {
                    entity.Add<HandedTo>() = new HandedTo(handingOff.To);
                    RememberHanded(world, handlerType.Id, header.Key, handingOff.To);
                    entity.Add<Removing>() = new Removing(RpcStatus.Ok, null); // its mail follows the record
                }
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
    /// record, versions settle who wins), for a fenced copy (it goes without a write), and for a new one while this shard
    /// stops (a copy already waiting takes it and hands it on as it leaves).
    /// </summary>
    private static bool Restore(World world, HandlerTypeInfo handlerType, long key, ReadOnlySpan<byte> payload, bool loaded, Entity entity,
        ulong from)
    {
        if (loaded ? !entity.Has<AwaitingHandoff>() || entity.Has<Fenced>() // fenced: it goes without a write, the record with it
                : !ShardDispatch.Owns(world, handlerType.Id, key) && !SendersOwn(world, handlerType.Id, key, from))
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

        entity.Add<TakenHandoff>() = new TakenHandoff(IdOf(payload)); // read: the same record again is answered "accepted"
        entity.Add<RestoredRecord>() = new RestoredRecord(payload.ToArray()); // until OnLoad ends (cold: one per move)
        if (store.Enabled && payload[CriticalAt] == 1) entity.Add<SaveNow>();
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

    private static bool IsHandoff(uint op) => op == Record || op == Resent || op == Request || op == None || op == Answer || op == MovedOn;

    /// <summary>The handoff a record belongs to (its first send's id, kept by resends).</summary>
    private static ulong IdOf(ReadOnlySpan<byte> payload) => BinaryPrimitives.ReadUInt64LittleEndian(payload[(2 * sizeof(ulong) + sizeof(long))..]);

    /// <summary>Remember for the move window that the record of a key went to <paramref name="to"/>.</summary>
    private static void RememberHanded(World world, uint handlerTypeId, long key, ulong to)
    {
        ref var seen = ref world.GetSingleton<HandoffsSeen>();
        long now = world.GetSingleton<ShardActivity>().NowMs;
        while (seen.HandedOrder.TryPeek(out var oldest) && (oldest.UntilMs < now || seen.HandedOrder.Count >= HandoffsSeen.Max))
        {
            seen.HandedOrder.Dequeue();
            if (seen.Handed.TryGetValue(oldest.Address, out var entry) && entry.UntilMs == oldest.UntilMs) seen.Handed.Remove(oldest.Address);
        }

        long until = now + MoveWindowMs(world.Shard.Node.Options);
        var address = new ActorAddress(handlerTypeId, key);
        seen.Handed[address] = new SeenHandoff(to, until);
        seen.HandedOrder.Enqueue((address, until));
    }

    /// <summary>Where this shard handed the key's record within the move window, or null.</summary>
    private static ulong? HandedTo(World world, uint handlerTypeId, long key) =>
        world.GetSingleton<HandoffsSeen>().Handed.TryGetValue(new ActorAddress(handlerTypeId, key), out var entry) &&
        entry.UntilMs >= world.GetSingleton<ShardActivity>().NowMs ? entry.PassedTo : null;

    /// <summary>Remember a handoff taken here (<paramref name="passedTo"/> 0) or passed on, for the resends' window.</summary>
    private static void Seen(World world, ulong id, ulong passedTo)
    {
        ref var seen = ref world.GetSingleton<HandoffsSeen>();
        long now = world.GetSingleton<ShardActivity>().NowMs;
        while (seen.Order.TryPeek(out var oldest) && (oldest.UntilMs < now || seen.Order.Count >= HandoffsSeen.Max))
        {
            seen.Order.Dequeue();
            seen.ById.Remove(oldest.Id);
            if (oldest.UntilMs >= now) seen.ForgotUntilMs = oldest.UntilMs; // forgotten early: "not seen" proves less until then
        }

        long until = now + 2 * TimeoutMs(world.Shard.Node);
        if (seen.ById.TryAdd(id, new SeenHandoff(passedTo, until))) seen.Order.Enqueue((id, until));
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
    /// every asker is told "none". The node the record went to, 0 when it went nowhere.
    /// </summary>
    private static ulong PassOn(World world, uint handlerTypeId, long key, ReadOnlySpan<byte> record, ulong from = 0)
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

        if (record.IsEmpty) return 0;
        if (to == 0 && node.View.HandlerTypeOwner(handlerTypeId, key) is { } owner && owner.NodeId != node.Id && owner.NodeId != from) to = owner.NodeId;
        return to != 0 && SendRecord(node, to, Record, handlerTypeId, key, record, hops: 1) ? to : 0;
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
