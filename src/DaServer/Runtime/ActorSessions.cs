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
    public static void Record(World world, Entity actor, Origin origin) =>
        SessionsOf(world, actor).Sessions.Add(new ClientSession(origin.Node, origin.Id));

    /// <summary>The actor binds or grants the session (<paramref name="change"/>), or withdraws it.</summary>
    public static void Change(World world, Entity actor, ClientSession session, AccessChange change)
    {
        var sessions = SessionsOf(world, actor);
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

    /// <summary>
    /// Close the session (a kick): its grant of this actor is withdrawn at once, so a resume of it already in this
    /// actor's mail is refused, then its node closes it and its actors get <see cref="SessionClosed"/> without a resume
    /// grace. Sending <see cref="Disconnect"/> from an actor is DAS020.
    /// </summary>
    public static void Disconnect(in Context context, ClientSession session)
    {
        if (context.Entity != default) Change(context.World, context.Entity, session, AccessChange.Revoke);
        context.Shard.Node.Send(session, new Disconnect());
    }

    private static ClientSessions SessionsOf(World world, Entity actor)
    {
        if (!actor.TryGet<ClientSessions>(out var sessions))
            actor.Set(sessions = world.GetSingleton<SessionPool>().Rent());
        return sessions;
    }

    /// <summary>A <see cref="SessionClosed"/> for the actor: forget the session; false when the close is stale or unreadable (not delivered).</summary>
    public static bool Closed(World world, Entity actor, ReadOnlySpan<byte> payload)
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
    private static bool Forget(Entity actor, ClientSession session)
    {
        if (!actor.TryGet<ClientSessions>(out var sessions)) return true;
        if (sessions.Superseded.Remove(session)) return false;
        sessions.Sessions.Remove(session);
        sessions.Granted.Remove(session);
        return true;
    }

    /// <summary>
    /// A client resumes <see cref="SessionResumed.Previous"/> on a new session: accepted only while the actor holds the
    /// previous session granted (not withdrawn, not closed, not resumed already: a ticket is good for one resume of what
    /// is still granted; resumed already answers <see cref="RpcStatus.Superseded"/>). The new session replaces it. A
    /// resume back to a session this actor superseded is the client-facing node undoing a resume whose new connection
    /// died before it completed (<paramref name="reverted"/>): the previous session is granted again, so the client can
    /// resume it once more.
    /// </summary>
    public static RpcStatus Resume(World world, Entity actor, in SessionResumed resumed, out bool reverted)
    {
        reverted = false;
        if (!actor.TryGet<ClientSessions>(out var sessions)) return RpcStatus.Forbidden;
        if (!sessions.Granted.Remove(resumed.Previous))
            return sessions.Superseded.Contains(resumed.Previous) ? RpcStatus.Superseded : RpcStatus.Forbidden;
        sessions.Sessions.Remove(resumed.Previous);
        sessions.Sessions.Add(resumed.Session);
        sessions.Granted.Add(resumed.Session);
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
        List<(Entity, ActorKey)>? targets = null;
        foreach (var item in world.Query<ClientSessions, ActorKey>())
        {
            var sessions = item.Component1.Sessions;
            item.Component1.Superseded.RemoveWhere(s => s.Node == nodeId); // their close will not come
            item.Component1.Granted.RemoveWhere(s => s.Node == nodeId);
            bool any = false;
            foreach (var session in sessions) any |= session.Node == nodeId;
            if (any) (targets ??= new()).Add((item.Entity, item.Component2));
        }

        // Delivered after the query: a handler may change the world's structure.
        foreach (var (entity, key) in targets ?? [])
        {
            if (!entity.IsAlive || !entity.TryGet<ClientSessions>(out var sessions)) continue;
            lost?.Clear();
            foreach (var session in sessions.Sessions)
                if (session.Node == nodeId) (lost ??= new()).Add(session);
            if (lost == null) continue;
            var handlerType = world.Shard.Node.HandlerTypeById(key.HandlerTypeId)!;
            bool handles = handlerType.Handlers.Of(TypeId<SessionClosed>.Value) != null;
            foreach (var session in lost)
            {
                sessions.Sessions.Remove(session);
                if (!handles) continue;
                var w = MessageWriter.Write(new SessionClosed(session));
                w.SetHeader(FrameKind.Message, key.HandlerTypeId, TypeId<SessionClosed>.Value, key.Key);
                ShardDispatch.Deliver(world, entity, key.Address, new Envelope(w, null));
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
