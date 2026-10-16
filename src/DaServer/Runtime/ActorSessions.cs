namespace DaServer.Runtime;

/// <summary>
/// The actor side of client sessions, on every node hosting actors (ADR-050): each loaded actor keeps the sessions it
/// knows (<see cref="ClientSessions"/>: its callers and the sessions it bound), so a session that ends reaches it as
/// <see cref="SessionClosed"/> even when the node holding the session crashed, and the sessions it holds granted, the
/// only ones a client may resume from (<see cref="Resume"/>). Closes are handled by a dispatch filter, resumes by the
/// runtime before the actor's handler runs; other messages pay nothing.
/// </summary>
internal static class ActorSessions
{
    static ActorSessions()
    {
        MessageFilters.Register<SessionClosed>(static (object target, in SessionClosed closed, in CallContext _) =>
            target is Actor actor && !Forget(actor, closed.Session) ? RpcStatus.NotHosted : RpcStatus.Ok);
    }

    /// <summary>Install the filters (once per process; nodes call it when they start).</summary>
    public static void Register() => RuntimeHelpers.RunClassConstructor(typeof(ActorSessions).TypeHandle);

    /// <summary>Remember a client session of a loaded actor (a hash lookup; allocates once per new session).</summary>
    public static void Record(Actor actor, ClientSession session) => SessionsOf(actor).Sessions.Add(session);

    /// <summary>The actor binds or grants the session (<paramref name="change"/>), or withdraws it.</summary>
    public static void Change(Actor actor, ClientSession session, AccessChange change)
    {
        var sessions = SessionsOf(actor);
        if (change is AccessChange.Bind or AccessChange.Grant)
        {
            sessions.Sessions.Add(session);
            sessions.Granted.Add(session);
        }
        else
        {
            sessions.Granted.Remove(session);
        }
    }

    private static ClientSessions SessionsOf(Actor actor)
    {
        var entity = actor.Entity;
        if (!entity.TryGet<ClientSessions>(out var sessions))
            entity.Add<ClientSessions>() = sessions = actor.World.GetSingleton<ActorPools>().RentSessions();
        return sessions;
    }

    /// <summary>Forget a closed session; false when it had been resumed (its close is stale and not delivered).</summary>
    private static bool Forget(Actor actor, ClientSession session)
    {
        if (!actor.Entity.TryGet<ClientSessions>(out var sessions)) return true;
        if (sessions.Superseded.Remove(session)) return false;
        sessions.Sessions.Remove(session);
        sessions.Granted.Remove(session);
        return true;
    }

    /// <summary>
    /// A client resumes <see cref="SessionResumed.Previous"/> on a new session: accepted only while the actor holds the
    /// previous session granted (not withdrawn, not closed, not resumed already: a ticket is good for one resume of what
    /// is still granted). The new session replaces it and is the caller of the turn (<c>Caller</c>).
    /// </summary>
    public static bool Resume(Actor actor, in SessionResumed resumed)
    {
        if (!actor.Entity.TryGet<ClientSessions>(out var sessions) || !sessions.Granted.Remove(resumed.Previous)) return false;
        sessions.Sessions.Remove(resumed.Previous);
        sessions.Superseded.Add(resumed.Previous); // removed when its late SessionClosed arrives or its node dies
        sessions.Sessions.Add(resumed.Session);
        sessions.Granted.Add(resumed.Session);
        actor.TurnCaller = new Origin(resumed.Session.Node, resumed.Session.Id);
        return true;
    }

    /// <summary>
    /// A node holding sessions died without closing them: every loaded actor of this shard that knew one of them
    /// receives <see cref="SessionClosed"/> (when it handles it), as if the session had been closed.
    /// </summary>
    public static void CloseSessionsOf(World world, ulong nodeId)
    {
        List<ClientSession>? lost = null;
        foreach (var item in world.Query<ClientSessions, ActorRef>())
        {
            var sessions = item.Component1.Sessions;
            item.Component1.Superseded.RemoveWhere(s => s.Node == nodeId); // their close will not come
            item.Component1.Granted.RemoveWhere(s => s.Node == nodeId);
            lost?.Clear();
            foreach (var session in sessions)
                if (session.Node == nodeId) (lost ??= new()).Add(session);
            if (lost == null || lost.Count == 0) continue;

            var actor = item.Component2.Actor;
            bool handles = actor.HandlerType.Handlers.Of(MessageType<SessionClosed>.WireId) != null;
            foreach (var session in lost)
            {
                sessions.Remove(session);
                if (!handles) continue;
                var w = MessageWriter.Write(new SessionClosed(session));
                w.SetHeader(FrameKind.Message, actor.HandlerType.Id, MessageType<SessionClosed>.WireId, actor.Key);
                ShardDispatch.Deliver(world, item.Entity, new Envelope(w, null));
            }
        }
    }
}
