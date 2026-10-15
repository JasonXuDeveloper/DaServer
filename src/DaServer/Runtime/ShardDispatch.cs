namespace DaServer.Runtime;

/// <summary>
/// Operations shared by the runtime systems (routing a frame, delivering and invoking a message). Static and
/// stateless: everything they touch lives in the world.
/// </summary>
internal static class ShardDispatch
{
    /// <summary>Serialized size of a <see cref="SessionRef"/> caller prefix.</summary>
    internal static readonly int SessionRefSize = NinoSerializer.Serialize(default(SessionRef)).Length;

    /// <summary>
    /// Route a received message (one-way or request): services run immediately; actor messages go to the owner's
    /// mailbox (loading it on first use) or are forwarded when another node owns the key. Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    public static void Route(World world, FrameWriter frame, IReplySink? sink)
    {
        var node = world.Shard().Node;
        var header = FrameHeader.Read(frame.Frame);
        uint target = FrameHeader.TargetOf(in header, frame.Frame);
        var handlerType = target != 0 ? node.HandlerTypeById(target) : node.ServiceHandler(header.MessageId);
        var handler = handlerType?.Handlers.Of(header.MessageId);
        if (handler == null)
        {
            // Not hosted here (stale view at the sender): an actor message finds its owner, a service request fails.
            // An actor type that does not handle the message fails at its owner the same way (wrong id type).
            if (target == 0 || (handlerType != null && handlerType.IsActor))
                Fail(frame, sink, RpcStatus.NotHosted, "Nothing here handles the message");
            else
                node.Forward(frame, sink);
            return;
        }

        if (!handlerType!.IsActor)
            Track(world, Invoke(handler.Dispatcher, ServiceOf(world, handlerType), frame, sink, world.Logger()));
        else if (TryGetLoaded(world, handlerType.Id, header.Key, out var existing))
            Deliver(world, existing, new Envelope(frame, sink)); // even while unloading: its mail moves in order
        else if (!OwnsHere(world, node, handlerType.Id, header.Key))
            node.Forward(frame, sink);
        else
            Deliver(world, ActorOf(world, handlerType, header.Key), new Envelope(frame, sink));
    }

    private static bool OwnsHere(World world, Node node, uint handlerTypeId, long key) =>
        node.IsLocalOwner(handlerTypeId, key) && !world.GetSingleton<ShardActivity>().Stopping;

    private static object ServiceOf(World world, HandlerTypeInfo handlerType) =>
        world.GetSingleton<ServiceDirectory>().Services[handlerType.Id];

    /// <summary>A loaded actor of this shard, including one being unloaded (new mail queues behind its mail).</summary>
    private static bool TryGetLoaded(World world, uint handlerTypeId, long key, out Entity actor) =>
        world.GetSingleton<ActorDirectory>().Actors.TryGetValue(new ActorAddress(handlerTypeId, key), out actor);

    /// <summary>The loaded actor of (<paramref name="handlerType"/>, <paramref name="key"/>) on this shard, loaded on first use.</summary>
    private static Entity ActorOf(World world, HandlerTypeInfo handlerType, long key)
    {
        var id = new ActorAddress(handlerType.Id, key);
        var actors = world.GetSingleton<ActorDirectory>().Actors;
        if (!actors.TryGetValue(id, out var entity))
            actors[id] = entity = Load(world, handlerType, id);
        return entity;
    }

    /// <summary>
    /// Queue a message for an actor and tag it <see cref="Ready"/> (it runs once it has no turn). A full
    /// mailbox (<see cref="NodeOptions.ActorMailboxCapacity"/>) refuses the message: one slow actor cannot hold the
    /// shard's memory.
    /// </summary>
    public static void Deliver(World world, Entity actor, in Envelope envelope)
    {
        ref var mailbox = ref actor.Get<Mailbox>();
        var shard = world.Shard();
        if (mailbox.Pending.Count >= shard.Node.Options.ActorMailboxCapacity)
        {
            shard.CountRejected();
            Overloaded(envelope.Frame, envelope.Sink);
            return;
        }

        mailbox.Pending.Enqueue(envelope);
        if (!actor.Has<Ready>()) actor.Add<Ready>();
    }

    /// <summary>
    /// Create the actor's entity (link, identity, mailbox, the actor's own components) and start
    /// <see cref="Actor.OnLoad"/> as its first turn (<see cref="Loading"/>): mail waits until it completes.
    /// </summary>
    private static Entity Load(World world, HandlerTypeInfo handlerType, ActorAddress id)
    {
        var actor = (Actor)handlerType.Factory();
        actor.HandlerType = handlerType;
        actor.Key = id.Key;
        actor.Shard = world.Shard();
        var entity = world.CreateEntity(new ActorRef(actor), new Mailbox(new()) { LastActive = Environment.TickCount64 },
            new Loading());
        actor.Entity = entity;
        var idle = actor.UnloadAfterIdle;
        if (idle > TimeSpan.Zero) entity.Add<UnloadWhenIdle>() = new UnloadWhenIdle((long)idle.TotalMilliseconds);
        actor.InitializeEntity(entity);
        TurnWatcher.Begin(world, entity, Start(static a => a.OnLoad(), actor));
        return entity;
    }

    /// <summary>Start a lifecycle hook, turning a synchronous throw into a faulted task.</summary>
    public static System.Threading.Tasks.ValueTask Start(Func<Actor, System.Threading.Tasks.ValueTask> hook, Actor actor)
    {
        try
        {
            return hook(actor);
        }
        catch (Exception e)
        {
            return System.Threading.Tasks.ValueTask.FromException(e);
        }
    }

    /// <summary>
    /// Deserialize and invoke a message on <paramref name="target"/> through a generated dispatcher. Returns the
    /// handler's task (default when it completed synchronously). A synchronous failure is replied to the caller, or
    /// logged when nobody waits for a reply (one-way call, event). Disposes <paramref name="frame"/>.
    /// </summary>
    public static System.Threading.Tasks.ValueTask Invoke(MessageDispatcher dispatcher, object target, FrameWriter frame,
        IReplySink? sink, ILogger logger)
    {
        try
        {
            if (frame.Deadline != 0 && Environment.TickCount64 > frame.Deadline)
            {
                Expired(); // the caller gave up while the request waited: running it would be wasted (and late) work
                return default;
            }

            var header = FrameHeader.Read(frame.Frame);
            uint requestId = header.Kind == FrameKind.Request ? header.RequestId : 0;
            var payload = frame.Payload;
            var caller = default(SessionRef);
            if ((header.Flags & FrameFlags.HasCaller) != 0)
            {
                var callerReader = new Reader(payload);
                NinoDeserializer.Deserialize(out caller, ref callerReader);
                payload = payload.Slice(SessionRefSize);
            }

            var context = new CallContext(sink, requestId, caller);
            var previous = CallContext.Enter(in context);
            try
            {
                var reader = new Reader(payload);
                var task = dispatcher(target, ref reader, in context);
                if (!task.IsCompleted) return task;
                task.GetAwaiter().GetResult();
                return default;
            }
            catch (Exception e)
            {
                if (context.ExpectsReply) context.ReplyException(e);
                else LogUnobserved(logger, e, target, in header);
                return default;
            }
            finally
            {
                CallContext.Exit(in previous);
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Expired() => Shard.Current?.CountExpired();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogUnobserved(ILogger logger, Exception e, object target, in FrameHeader header) =>
        logger.LogError(e, "{Kind} {Id:x8} failed in {Target}", header.Kind, header.MessageId, target);

    /// <summary>
    /// Invoke the next message of an actor. Records the client session of a gateway-relayed message (see
    /// <see cref="ClientSessions"/>), forgets a closed one and swaps a resumed one; the late close of a resumed session
    /// is not delivered.
    /// </summary>
    public static System.Threading.Tasks.ValueTask Invoke(Actor actor, in Envelope envelope)
    {
        var header = FrameHeader.Read(envelope.Frame.Frame);
        actor.TurnCaller = default;
        if (header.MessageId == MessageType<SessionClosed>.Id)
        {
            if (!ForgetSession(actor, envelope.Frame.Payload))
            {
                envelope.Frame.Dispose();
                return default;
            }
        }
        else if (header.MessageId == MessageType<SessionResync>.Id) ResyncSession(actor, envelope.Frame.Payload);
        else if ((header.Flags & FrameFlags.HasCaller) != 0)
        {
            var reader = new Reader(envelope.Frame.Payload);
            NinoDeserializer.Deserialize(out actor.TurnCaller, ref reader);
            RecordSession(actor.Entity, actor.TurnCaller);
        }

        var dispatcher = actor.HandlerType.Handlers.Of(header.MessageId)!.Dispatcher; // validated by Route
        return Invoke(dispatcher, actor, envelope.Frame, envelope.Sink, actor.Shard.Node.Logger);
    }

    /// <summary>Remember a client session of a loaded actor (a hash lookup; allocates once per new session).</summary>
    internal static void RecordSession(Entity actor, SessionRef session)
    {
        SessionsOf(actor).Sessions.Add(session);
    }

    private static ClientSessions SessionsOf(Entity actor)
    {
        if (!actor.TryGet<ClientSessions>(out var sessions))
            actor.Add<ClientSessions>() = sessions = new ClientSessions(new HashSet<SessionRef>(), new HashSet<SessionRef>());
        return sessions;
    }

    /// <summary>Forget a closed session; false when it had been resumed (its close is stale and not delivered).</summary>
    private static bool ForgetSession(Actor actor, ReadOnlySpan<byte> payload)
    {
        if (!actor.Entity.TryGet<ClientSessions>(out var sessions)) return true;
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out SessionClosed closed, ref reader);
        if (sessions.Superseded.Remove(closed.Session)) return false;
        sessions.Sessions.Remove(closed.Session);
        return true;
    }

    /// <summary>The resumed session replaces the previous one; it is the session of the turn (<c>CallerSession</c>).</summary>
    private static void ResyncSession(Actor actor, ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out SessionResync resync, ref reader);
        var sessions = SessionsOf(actor.Entity);
        sessions.Sessions.Remove(resync.Previous);
        sessions.Superseded.Add(resync.Previous); // removed when its late SessionClosed arrives or its gateway dies
        sessions.Sessions.Add(resync.Session);
        actor.TurnCaller = resync.Session;
    }

    /// <summary>
    /// A gateway died without closing its sessions: every loaded actor of this shard that knew one of them receives
    /// <see cref="SessionClosed"/> (when it handles it), as if the gateway had closed the session.
    /// </summary>
    internal static void CloseSessionsOf(World world, ulong gatewayNodeId)
    {
        List<SessionRef>? lost = null;
        foreach (var item in world.Query<ClientSessions, ActorRef>())
        {
            var sessions = item.Component1.Sessions;
            item.Component1.Superseded.RemoveWhere(s => s.GatewayNodeId == gatewayNodeId); // their close will not come
            lost?.Clear();
            foreach (var session in sessions)
                if (session.GatewayNodeId == gatewayNodeId) (lost ??= new()).Add(session);
            if (lost == null || lost.Count == 0) continue;

            var actor = item.Component2.Actor;
            bool handles = actor.HandlerType.Handlers.Of(MessageType<SessionClosed>.Id) != null;
            foreach (var session in lost)
            {
                sessions.Remove(session);
                if (!handles) continue;
                var w = MessageWriter.Write(new SessionClosed(session));
                w.SetHeader(FrameKind.Message, actor.HandlerType.Id, MessageType<SessionClosed>.Id, actor.Key);
                Deliver(world, item.Entity, new Envelope(w, null));
            }
        }
    }

    /// <summary>
    /// Keep an unfinished service call until it completes: it becomes a <see cref="ServiceCall"/> entity whose
    /// <see cref="Turn"/> ends like an actor's (its state machine returns to its pool then).
    /// </summary>
    private static void Track(World world, System.Threading.Tasks.ValueTask task)
    {
        if (!task.IsCompleted) TurnWatcher.Begin(world, world.CreateEntity(new ServiceCall()), task);
    }

    /// <summary>Refuse a call or event that found its shard or mailbox full (calls get <see cref="RpcStatus.Overloaded"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Overloaded(FrameWriter frame, IReplySink? sink) => Fail(frame, sink, RpcStatus.Overloaded, "Overloaded");

    /// <summary>Answer a request with an error status (one-way messages are dropped). Disposes the frame.</summary>
    internal static void Fail(FrameWriter frame, IReplySink? sink, RpcStatus status, string message)
    {
        var header = FrameHeader.Read(frame.Frame);
        frame.Dispose();
        if (sink != null && header.Kind == FrameKind.Request && header.RequestId != 0)
            sink.SendError(header.RequestId, status, message);
    }

    public static ILogger Logger(this World world) => world.Shard().Node.Logger;
}
