namespace DaServer.Persistence;

// Receiving handoff frames (ADR-072): records taken, refused or passed on, requests, answers and "none"; requests a
// shard cannot answer yet wait (Defer) until ownership settles.
internal static partial class Handoff
{
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
            bool loaded = ShardDispatch.TryGetLoaded(world, new ActorAddress(handlerType.Id, header.Key), out var entity);
            if (op == Record || op == Resent)
            {
                ulong id = IdOf(payload);
                ref var seen = ref world.HandoffsSeen;
                if (loaded && entity.TryGetTakenHandoff(out var taken) && taken.Id == id)
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
                        ? entity.TryGetAwaitingHandoff(out var awaiting) && awaiting.From == from
                        : header.Hops == 0 && world.ShardActivity.NowMs > seen.ForgotUntilMs && ShardDispatch.Owns(world, handlerType.Id, header.Key);
                    bool askAgain = false, again = takes && Restore(world, handlerType, header.Key, payload, loaded, entity, from, out askAgain);
                    if (again) Seen(world, id, 0);
                    Notify(node, from, Answer, handlerType.Id, header.Key, again ? (byte)1 : (byte)0);
                    if (askAgain) Await(world, entity, from); // after the refusal on the same link: see Restore
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

                bool restored = Restore(world, handlerType, header.Key, payload, loaded, entity, from, out bool reask);
                if (restored) Seen(world, id, 0);
                Notify(node, from, Answer, handlerType.Id, header.Key, restored ? (byte)1 : (byte)0);
                if (reask) Await(world, entity, from); // after the refusal on the same link: see Restore
            }
            else if (op == Request)
            {
                if (!loaded && !ShardDispatch.Owns(world, handlerType.Id, header.Key) && HandedRecordTo(world, handlerType.Id, header.Key) is { } handed && handed != from)
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
                else if (entity.TryGetHandingOff(out var handingOff) && handingOff.To == from)
                    Resend(node, new ActorAddress(handlerType.Id, header.Key), from, handingOff.Record); // the asker never got it
                else
                {
                    if (!entity.HasHandoffWanted) entity.SetHandoffWanted(from); // told when it goes
                    // A copy this node runs for a key the asker owns in this node's view (adopted from a sender ahead of
                    // this view, which then moved back): it leaves, or it would stay until this node's next view change.
                    if (!ShardDispatch.Owns(world, handlerType.Id, header.Key) && node.View.HandlerTypeOwner(handlerType.Id, header.Key)?.NodeId == from &&
                        !entity.IsUnloadRequested && !entity.IsLeaving && !entity.HasRemoving)
                        entity.SetUnloadRequested();
                }
            }
            else if (op == None)
            {
                if (loaded && entity.TryGetAwaitingHandoff(out var asked) && asked.From == from) // the node it asked, not a late one
                {
                    entity.RemoveAwaitingHandoff();
                    ShardDispatch.BeginLoad(world, entity, new ActorAddress(handlerType.Id, header.Key));
                }
                else if (!loaded && !ShardDispatch.Owns(world, handlerType.Id, header.Key)) PassOn(world, handlerType.Id, header.Key, default);
            }
            else if (op == MovedOn)
            {
                // The node asked gave the record to another: wait for that one instead (it answers when its copy goes).
                if (loaded && entity.TryGetAwaitingHandoff(out var asked) && asked.From == from)
                {
                    ulong to = BinaryPrimitives.ReadUInt64LittleEndian(payload[sizeof(ulong)..]);
                    if (to == node.Id || !Notify(node, to, Request, handlerType.Id, header.Key, 0))
                    {
                        entity.RemoveAwaitingHandoff(); // it came here after all, or that node is gone: the store
                        ShardDispatch.BeginLoad(world, entity, new ActorAddress(handlerType.Id, header.Key));
                    }
                    else entity.AwaitingHandoff = new AwaitingHandoff(to, world.ShardActivity.NowMs + TimeoutMs(node));
                }
            }
            else if (loaded && entity.TryGetHandingOff(out var handingOff))
            {
                entity.RemoveHandingOff();
                if (payload[sizeof(ulong)] == 1)
                {
                    entity.SetHandedTo(handingOff.To);
                    RememberHanded(world, handlerType.Id, header.Key, handingOff.To);
                    entity.SetRemoving(RpcStatus.Ok); // its mail follows the record
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
    /// stops (a copy already waiting takes it and hands it on as it leaves). Refused too when the record does not read:
    /// the sender then takes its state back and writes it. A copy with mail that refuses so (<paramref name="reask"/>) asks
    /// the sender again after the refusal: on the same ordered link the sender's copy writes first and answers "none"
    /// once it is gone, so the copy reads the written record instead of racing the write (versions would fence it).
    /// </summary>
    private static bool Restore(World world, HandlerTypeInfo handlerType, long key, ReadOnlySpan<byte> payload, bool loaded, Entity entity,
        ulong from, out bool reask)
    {
        reask = false;
        if (loaded ? !entity.HasAwaitingHandoff || entity.IsFenced // fenced: it goes without a write, the record with it
                : !ShardDispatch.Owns(world, handlerType.Id, key) && !SendersOwn(world, handlerType.Id, key, from))
            return false;
        if (loaded) entity.RemoveAwaitingHandoff();
        var store = world.ShardStore;
        long every = store.Enabled ? handlerType.Settings.SaveEverySeconds * 1000L : 0;
        if (!loaded) entity = ShardDispatch.Adopt(world, handlerType, key);
        try
        {
            Records.Read(entity, payload[RecordHeader..], store.ById);
        }
        catch (Exception e)
        {
            // Refused: its sender takes the state back and writes it, instead of this node holding a copy that never runs.
            Log.HandoffUnreadable(world.Logger, e, handlerType);
            if (loaded) reask = true;
            else entity.SetRemoving(RpcStatus.Ok); // nothing queued yet
            return false;
        }

        entity.SetTakenHandoff(IdOf(payload)); // read: the same record again is answered "accepted"
        ForgetHanded(world, handlerType.Id, key); // the key is here again: an asker is no longer sent away
        if (store.Enabled && payload[CriticalAt] == 1) entity.SetSaveNow();
        if (store.Enabled)
        {
            entity.Set(new Stored
            {
                Version = BinaryPrimitives.ReadInt64LittleEndian(payload[sizeof(ulong)..]),
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(payload[(sizeof(ulong) + sizeof(long))..]), // changes since are written
                Attempt = BinaryPrimitives.ReadUInt64LittleEndian(payload[AttemptAt..]),
                EveryMs = every,
                DueMs = world.ShardActivity.NowMs + every,
            });
        }

        if (!ShardDispatch.BeginLive(world, entity, restored: true))
        {
            if (entity.HasStored)
            {
                // Restored from a handoff its sender let go: the record is the only copy of the moved state, so it is
                // written as it came (not as the failed handler left it) before the copy goes; its mail loads it again.
                RestoreAsReceived(world, entity, payload);
                entity.Set(new Leaving(), new FinalSave());
            }
            else entity.SetRemoving(RpcStatus.Error);
        }

        return true;
    }

    private static bool IsHandoff(uint op) => op == Record || op == Resent || op == Request || op == None || op == Answer || op == MovedOn;

    private static void Defer(World world, uint handlerTypeId, long key, ulong by)
    {
        var waiting = world.HandoffAsks.Waiting;
        long now = world.ShardActivity.NowMs;
        if (waiting.Count >= HandoffAsks.Max)
            for (int i = waiting.Count - 1; i >= 0; i--)
                if (waiting[i].UntilMs < now)
                {
                    waiting.SwapRemoveAt(i);
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
        var waiting = world.HandoffAsks.Waiting;
        ulong to = 0;
        for (int i = waiting.Count - 1; i >= 0 && (record.IsEmpty || to == 0); i--)
        {
            var ask = waiting[i];
            if (ask.HandlerTypeId != handlerTypeId || ask.Key != key) continue;
            waiting.SwapRemoveAt(i);
            if (record.IsEmpty) Gone(node, handlerTypeId, key, ask.By);
            else to = ask.By; // one record, one asker: the others ask again and find it there
        }

        if (record.IsEmpty) return 0;
        if (to == 0 && node.View.HandlerTypeOwner(handlerTypeId, key) is { } owner && owner.NodeId != node.Id && owner.NodeId != from) to = owner.NodeId;
        return to != 0 && SendRecord(node, to, Record, handlerTypeId, key, record, hops: 1) ? to : 0;
    }

    private static bool SendersOwn(World world, uint handlerTypeId, long key, ulong from) =>
        !world.ShardActivity.Stopping && world.Shard.Node.View.HandlerTypeOwner(handlerTypeId, key)?.NodeId == from;

    private static bool Notify(Node node, ulong to, uint op, uint handlerTypeId, long key, byte value) =>
        Notify(node, to, op, handlerTypeId, key, value, 1);
}
