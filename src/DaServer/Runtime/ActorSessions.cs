namespace DaServer.Runtime;

/// <summary>
/// The actor side of client sessions, on every node hosting actors (ADR-050): each loaded actor keeps the sessions it
/// knows (<see cref="ClientSessions"/>: its callers and the sessions it bound), so a session that ends reaches it as
/// <see cref="SessionClosed"/> even when the node holding the session crashed, and the sessions it holds granted, the
/// only ones a client may resume from (<see cref="Resume"/>). Closes and resumes are handled by the runtime before the
/// actor's handler runs (not by a filter: a user's filter could refuse them); other messages pay one id comparison.
/// </summary>
internal static class ActorSessions
{

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

    /// <summary>A <see cref="SessionClosed"/> for the actor: forget the session; false when the close is stale or unreadable (not delivered).</summary>
    public static bool Closed(Actor actor, ReadOnlySpan<byte> payload)
    {
        SessionClosed closed;
        try
        {
            closed = NinoDeserializer.Deserialize<SessionClosed>(payload);
        }
        catch (Exception)
        {
            return false; // not a close: refused like a stale one (only a peer or an HTTP send could write it)
        }

        return Forget(actor, closed.Session);
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
    /// is still granted; resumed already answers <see cref="RpcStatus.Superseded"/>). The new session replaces it and is
    /// the caller of the turn (<c>Caller</c>). A resume back to a session this actor superseded is the client-facing
    /// node undoing a resume whose new connection died before it completed (<paramref name="reverted"/>): the previous
    /// session is granted again, so the client can resume it once more.
    /// </summary>
    public static RpcStatus Resume(Actor actor, in SessionResumed resumed, out bool reverted)
    {
        reverted = false;
        if (!actor.Entity.TryGet<ClientSessions>(out var sessions)) return RpcStatus.Forbidden;
        if (!sessions.Granted.Remove(resumed.Previous))
            return sessions.Superseded.Contains(resumed.Previous) ? RpcStatus.Superseded : RpcStatus.Forbidden;
        sessions.Sessions.Remove(resumed.Previous);
        sessions.Sessions.Add(resumed.Session);
        sessions.Granted.Add(resumed.Session);
        actor.TurnCaller = new Origin(resumed.Session.Node, resumed.Session.Id);
        reverted = sessions.Superseded.Remove(resumed.Session);
        if (!reverted) sessions.Superseded.Add(resumed.Previous); // removed when its late SessionClosed arrives or its node dies
        return RpcStatus.Ok;
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
            bool handles = actor.HandlerType.Handlers.Of(TypeId<SessionClosed>.Value) != null;
            foreach (var session in lost)
            {
                sessions.Remove(session);
                if (!handles) continue;
                var w = MessageWriter.Write(new SessionClosed(session));
                w.SetHeader(FrameKind.Message, actor.HandlerType.Id, TypeId<SessionClosed>.Value, actor.Key);
                ShardDispatch.Deliver(world, item.Entity, new Envelope(w, null));
            }
        }
    }
}

/// <summary>
/// The client-facing nodes of the cluster as this node sees them: when one is gone (dead or left), the sessions it held
/// are closed on every shard once their clients had <see cref="NodeOptions.ResumeGrace"/> to resume elsewhere. Runs on
/// every node hosting actors, not only client-facing ones: it is the actors' side of sessions, not a client feature.
/// </summary>
internal sealed class SessionNodes(Node node)
{
    private ulong[] _known = [];

    /// <summary>A new view (the node's view-change thread).</summary>
    public void OnViewChanged(ClusterView view)
    {
        var current = new List<ulong>();
        foreach (var member in view.Members)
            if (member.HasScope(AddressScopes.Session) && member.Status != MemberStatus.Dead) current.Add(member.NodeId);
        foreach (ulong previous in _known)
        {
            if (current.Contains(previous)) continue;
            Log.ClientNodeGone(node.Logger, previous);
            _ = CloseAfterGrace(previous);
        }

        _known = current.ToArray();
    }

    private async Task CloseAfterGrace(ulong clientNode)
    {
        var grace = node.Options.EffectiveResumeGrace; // tickets need the secret: without one nothing resumes
        if (grace > TimeSpan.Zero) await Task.Delay(grace, node.Options.TimeProvider).ConfigureAwait(false);
        // Declared dead falsely (a pause, a blip), it refuted and is back: its clients never disconnected and never
        // resume, so their sessions are open, not lost.
        if (node.View.TryGetMember(clientNode, out var member) && member.Status is MemberStatus.Up or MemberStatus.Suspect) return;
        if (node.Running) node.PostToEveryShard(static (world, gone) => ActorSessions.CloseSessionsOf(world, gone), clientNode);
    }
}
