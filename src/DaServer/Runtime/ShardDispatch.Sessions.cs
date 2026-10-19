namespace DaServer.Runtime;

// Client sessions in dispatch: resumes and closes reach every granted actor, whether it handles them or not.
internal static partial class ShardDispatch
{
    /// <summary>
    /// A resume, or a close the actor does not handle, goes to every granted actor whether it handles the message or not:
    /// to its copy here, on its node, or behind the handoff record on its way here (which may hold the session). An actor
    /// neither loaded nor arriving holds no session, so it is not loaded for one: <paramref name="refusal"/>.
    /// </summary>
    private static void RouteSession(World world, HandlerTypeInfo handlerType, FrameWriter frame, IReplySink? sink, in FrameHeader header,
        RpcStatus refusal)
    {
        var node = world.Shard.Node;
        if (TryGetLoaded(world, handlerType.Id, header.Key, out var existing)) Deliver(world, existing, new Envelope(frame, sink));
        else if (!OwnsHere(world, node, handlerType.Id, header.Key)) node.Forward(frame, sink);
        else if (Handoff.PreviousOwner(world, handlerType.Id, header.Key) != null) Load(world, handlerType, header.Key, frame, sink);
        else Fail(frame, sink, refusal);
    }

    /// <summary>
    /// A client resumes a session at this actor (asked by the client-facing node): answered by the runtime, then
    /// delivered to the actor's handler, if any, as a one-way message from the new session.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static System.Threading.Tasks.ValueTask Resume(Actor actor, in Envelope envelope)
    {
        var frame = envelope.Frame;
        if (frame.Deadline != 0 && Platform.Milliseconds > frame.Deadline)
        {
            // The client-facing node answered its client without this actor (timed out): taking the session now would
            // leave the actor on a session that does not know it.
            Expired();
            frame.Dispose();
            return default;
        }

        SessionResumed resumed;
        try
        {
            resumed = NinoDeserializer.Deserialize<SessionResumed>(frame.Payload);
        }
        catch (Exception)
        {
            Fail(frame, envelope.Sink, RpcStatus.Forbidden); // not a resume (only a faulty peer could send it)
            return default;
        }

        var status = ActorSessions.Resume(actor, in resumed, out bool reverted);
        bool accepted = status == RpcStatus.Ok && !reverted; // a revert restores the session the actor had: nothing to tell
        if (accepted) actor.Shard.Node.Supersede(resumed.Previous, resumed.Session, actor.HandlerType.Id, actor.Key);
        else if (reverted) actor.Shard.Node.SwapGroup(resumed.Session, resumed.Previous, actor.HandlerType.Id, actor.Key); // the group back
        if (frame.CallId != 0 && envelope.Sink is { } sink)
        {
            if (status == RpcStatus.Ok) sink.SendOk(frame.CallId);
            else sink.SendError(frame.CallId, status);
        }

        if (accepted && actor.HandlerType.Handlers.Of(TypeId<SessionResumed>.Value) is { } handler)
            return Invoke(handler, actor, frame, null, actor.Shard.Node.Logger, actor.TurnChain = ChainOf(actor.Shard.World, frame));
        frame.Dispose();
        return default;
    }
}
