namespace DaServer.Runtime;

/// <summary>
/// The actor side of client sessions, on every node hosting actors (ADR-050): each loaded actor keeps the sessions it
/// knows (a <see cref="KnownSession"/> child entity per session: its callers and the sessions it bound), so a session that ends reaches it as
/// <see cref="SessionClosed"/> even when the node holding the session crashed, and the sessions it holds granted, the
/// only ones a client may resume from (<see cref="Resume"/>). Closes and resumes are handled by the runtime before the
/// actor's handler runs (not by a filter: a user's filter could refuse them); other messages pay one id comparison.
/// </summary>
internal static class ActorSessions
{
    /// <summary>Remember the client session a loaded actor's message came from (one index lookup; an entity the first time).</summary>
    public static void Record(World world, Entity actor, Origin origin)
    {
        var session = Of(world, actor, new ClientSession(origin.Node, origin.Id));
        if (!session.IsKnown) session.SetKnown();
    }

    /// <summary>The actor binds or grants the session (<paramref name="change"/>), or withdraws it.</summary>
    public static void Change(World world, Entity actor, ClientSession session, AccessChange change)
    {
        if (change is AccessChange.Bind or AccessChange.Grant) Of(world, actor, session).Set(new Known(), new Granted());
        else if (Find(world, actor, session, out var known)) known.RemoveGranted();
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

    /// <summary>The entity of <paramref name="session"/> under <paramref name="actor"/>, if the actor knows it.</summary>
    private static bool Find(World world, Entity actor, ClientSession session, out Entity known)
    {
        foreach (var candidate in world.KnownSessionIndex.Find(session))
            if (candidate.Read<ChildOf>().Parent == actor.Id)
            {
                known = candidate;
                return true;
            }

        known = default;
        return false;
    }

    private static Entity Of(World world, Entity actor, ClientSession session) =>
        Find(world, actor, session, out var known) ? known : world.CreateEntity(new KnownSession(session), new ChildOf(actor.Id));

    /// <summary>A session entity with no tag left is forgotten.</summary>
    private static void Prune(Entity known)
    {
        if (!known.IsKnown && !known.IsGranted && !known.IsSuperseded) known.Destroy();
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

        if (!Find(world, actor, closed.Session, out var known)) return true;
        if (known.IsSuperseded) // resumed since: its close is stale
        {
            known.RemoveSuperseded();
            Prune(known);
            return false;
        }

        known.Destroy();
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
        if (!Find(world, actor, resumed.Previous, out var previous)) return RpcStatus.Forbidden;
        if (!previous.IsGranted) return previous.IsSuperseded ? RpcStatus.Superseded : RpcStatus.Forbidden;
        previous.Remove<Known, Granted>();
        var current = Of(world, actor, resumed.Session);
        current.Set(new Known(), new Granted());
        reverted = current.IsSuperseded;
        if (reverted) current.RemoveSuperseded();
        else previous.SetSuperseded(); // removed when its late SessionClosed arrives or its node dies
        Prune(previous);
        return RpcStatus.Ok;
    }

    /// <summary>
    /// A node holding sessions died without closing them: every loaded actor of this shard that knew one of them
    /// receives <see cref="SessionClosed"/> (when it handles it), as if the session had been closed.
    /// </summary>
    public static void CloseSessionsOf(World world, ulong nodeId)
    {
        var lost = new List<(Entity Known, ClientSession Session, EntityId Actor)>(); // cold: once per lost client-facing node
        foreach (var item in world.Query<KnownSession, ChildOf>())
            if (item.KnownSession.Session.Node == nodeId) lost.Add((item.Entity, item.Component1.Session, item.Component2.Parent));

        foreach (var (known, session, actorId) in lost)
        {
            bool wasKnown = known.IsKnown;
            known.Destroy(); // its close will not come, and a resume from it neither
            if (!wasKnown || !world.TryGetEntity(actorId, out var actor)) continue;
            var key = actor.ActorKey;
            if (world.Shard.Node.HandlerTypeById(key.HandlerTypeId)!.Handlers.Of(TypeId<SessionClosed>.Value) == null) continue;
            var w = MessageWriter.Write(new SessionClosed(session));
            w.SetHeader(FrameKind.Message, key.HandlerTypeId, TypeId<SessionClosed>.Value, key.Key);
            ShardDispatch.Deliver(world, actor, key.Address, new Envelope(w, null));
        }
    }

    /// <summary>The sessions of <paramref name="actor"/> as its handoff record carries them.</summary>
    public static SessionsRecord? Collect(World world, Entity actor)
    {
        List<ClientSession>? known = null, granted = null, superseded = null;
        foreach (var child in world.Children(actor.Id))
        {
            if (!child.TryGetKnownSession(out var session)) continue;
            if (child.IsKnown) (known ??= []).Add(session.Session);
            if (child.IsGranted) (granted ??= []).Add(session.Session);
            if (child.IsSuperseded) (superseded ??= []).Add(session.Session);
        }

        return known == null && granted == null && superseded == null ? null
            : new SessionsRecord { Sessions = [.. known ?? []], Granted = [.. granted ?? []], Superseded = [.. superseded ?? []] };
    }

    /// <summary>Restore the sessions a handoff record carried onto <paramref name="actor"/>.</summary>
    public static void Restore(World world, Entity actor, SessionsRecord moved)
    {
        foreach (var s in moved.Sessions) Of(world, actor, s).SetKnown();
        foreach (var s in moved.Granted) Of(world, actor, s).SetGranted();
        foreach (var s in moved.Superseded) Of(world, actor, s).SetSuperseded();
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
