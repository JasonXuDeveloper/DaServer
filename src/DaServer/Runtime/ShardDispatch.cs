namespace DaServer.Runtime;

/// <summary>
/// Operations shared by the runtime systems (routing a frame, delivering and invoking a message). Static and
/// stateless: everything they touch lives in the world.
/// </summary>
internal static class ShardDispatch
{
    /// <summary>
    /// Route a received message (one-way or request): services run immediately; actor messages go to the owner's
    /// mailbox (loading it on first use) or are forwarded when another node owns the key. Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    public static void Route(World world, FrameWriter frame, IReplySink? sink)
    {
        var node = world.Shard().Node;
        var header = FrameHeader.Read(frame.Frame);
        var handlerType = node.HandlerTypeById(header.Target);
        if (header.MessageId == TypeId<SessionResumed>.Value && handlerType is { IsActor: true })
        {
            RouteResume(world, handlerType, frame, sink, in header);
            return;
        }

        var handler = handlerType?.Handlers.Of(header.MessageId);
        if (handler == null)
        {
            if (handlerType is { IsActor: true } && Handoff.TryReceive(world, handlerType, frame, in header)) return;
            // A handler type that does not handle the message (a wrong id type) fails; one not hosted here (stale view
            // at the sender) is found on its node.
            if (handlerType != null) Fail(frame, sink, RpcStatus.NotHosted);
            else node.Forward(frame, sink);
            return;
        }

        if (!handlerType!.IsActor)
            Track(world, Invoke(handler.Dispatcher, ServiceOf(world, handlerType), frame, sink, world.Logger(), ChainOf(world, frame)));
        else if (TryGetLoaded(world, handlerType.Id, header.Key, out var existing))
            Deliver(world, existing, new Envelope(frame, sink)); // even while unloading: its mail moves in order
        else if (!OwnsHere(world, node, handlerType.Id, header.Key))
            node.Forward(frame, sink);
        else
            Deliver(world, ActorOf(world, handlerType, header.Key), new Envelope(frame, sink));
    }

    /// <summary>
    /// A resume goes to every granted actor, whether it handles <see cref="SessionResumed"/> or not; one not loaded has no
    /// granted session, so it is refused without loading it.
    /// </summary>
    private static void RouteResume(World world, HandlerTypeInfo handlerType, FrameWriter frame, IReplySink? sink, in FrameHeader header)
    {
        var node = world.Shard().Node;
        if (TryGetLoaded(world, handlerType.Id, header.Key, out var existing)) Deliver(world, existing, new Envelope(frame, sink));
        else if (!OwnsHere(world, node, handlerType.Id, header.Key)) node.Forward(frame, sink);
        else Fail(frame, sink, RpcStatus.Forbidden);
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
        {
            actors[id] = entity = Create(world, handlerType, id);
            var actor = entity.Get<ActorRef>().Actor;
            if (Handoff.PreviousOwner(world, handlerType.Id, key) is { } from) Handoff.Await(world, entity, from);
            else BeginLoad(world, entity, actor);
        }

        return entity;
    }

    /// <summary>Whether this shard takes actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>) now (it owns it and is not stopping).</summary>
    internal static bool Owns(World world, uint handlerTypeId, long key) => OwnsHere(world, world.Shard().Node, handlerTypeId, key);

    /// <summary>An actor's entity, not started: <see cref="Handoff"/> restores what its previous owner sent before <see cref="Actor.OnLoad"/>.</summary>
    internal static Entity Adopt(World world, HandlerTypeInfo handlerType, long key)
    {
        var id = new ActorAddress(handlerType.Id, key);
        var entity = Create(world, handlerType, id);
        world.GetSingleton<ActorDirectory>().Actors[id] = entity;
        return entity;
    }

    /// <summary>The actor's record comes from the store (when it has one), then <see cref="Actor.OnLoad"/> runs.</summary>
    internal static void BeginLoad(World world, Entity entity, Actor actor)
    {
        if (world.GetSingleton<ShardStore>().Enabled) entity.Add<AwaitingRecord>(); // LoadSystem restores it, then OnLoad
        else TurnWatcher.Begin(world, entity, Start(static a => a.OnLoad().AsValueTask(), actor));
    }

    /// <summary>
    /// Queue a message for an actor and tag it <see cref="Ready"/> (it runs once it has no turn). A full
    /// mailbox (<see cref="NodeOptions.ActorMailboxCapacity"/>) refuses the message: one slow actor cannot hold the
    /// shard's memory.
    /// </summary>
    public static void Deliver(World world, Entity actor, in Envelope envelope)
    {
        long chain = envelope.Frame.Chain;
        if (chain != 0 && actor.Get<ActorRef>().Actor.TurnChain == chain && actor.TryGet<Turn>(out var turn) && !turn.Task.IsCompleted)
        {
            // The running turn waits for this request (directly or through others): queued, it would wait for the turn.
            // A turn that completed (it replied, its end is not processed yet) waits for nothing: the caller's next
            // request in the same chain queues.
            Fail(envelope.Frame, envelope.Sink, RpcStatus.CallCycle);
            return;
        }

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
    /// Create the actor's entity (link, identity, mailbox, the actor's own components), <see cref="Loading"/>: mail
    /// waits until its record is restored and <see cref="Actor.OnLoad"/> completed.
    /// </summary>
    private static Entity Create(World world, HandlerTypeInfo handlerType, ActorAddress id)
    {
        ref var pools = ref world.GetSingleton<ActorPools>();
        var actor = pools.RentActor(handlerType);
        actor.HandlerType = handlerType;
        actor.Key = id.Key;
        actor.Shard = world.Shard();
        var entity = world.CreateEntity(new ActorRef(actor), new Mailbox(pools.RentMailbox()) { LastActive = world.GetSingleton<ShardActivity>().NowMs },
            new Loading());
        actor.Entity = entity;
        var idle = actor.UnloadAfterIdle;
        if (idle > TimeSpan.Zero) entity.Add<UnloadWhenIdle>() = new UnloadWhenIdle((long)idle.TotalMilliseconds);
        actor.InitializeEntity(entity);
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
        IReplySink? sink, ILogger logger, long chain)
    {
        try
        {
            if (frame.Deadline != 0 && Platform.Milliseconds > frame.Deadline)
            {
                Expired(); // the caller gave up while the request waited: running it would be wasted (and late) work
                return default;
            }

            var header = FrameHeader.Read(frame.Frame);
            uint callId = frame.CallId;
            var context = new CallContext(sink, callId, frame.Origin);
            var previous = CallContext.Enter(in context);
            long previousChain = CallChain.Current;
            CallChain.Current = chain; // the requests this handler makes carry it (its awaits restore it, PendingCall)
            try
            {
                var task = dispatcher(target, frame.Payload, in context);
                if (!task.IsCompleted) return task;
                task.GetAwaiter().GetResult();
                return default;
            }
            catch (Exception e)
            {
                if (context.ExpectsReply) context.ReplyException(e);
                else LogUnobserved(logger, e, target, in header);
                Failed(target, e);
                return default;
            }
            finally
            {
                CallContext.Exit(in previous);
                CallChain.Current = previousChain;
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>The call chain a message runs in: the one its request carries, else a new one (ADR-059).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ChainOf(World world, FrameWriter frame)
    {
        long chain = frame.Chain;
        return chain != 0 ? chain : world.GetSingleton<CallChains>().Next();
    }

    /// <summary>
    /// A handler of <paramref name="target"/> failed (synchronously, or its turn faulted): count it, and apply the actor's
    /// <see cref="Actor.OnError"/> (C4). Reload removes the actor without saving once it is idle; its mail loads it again.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Failed(object target, Exception error)
    {
        Shard.Current?.CountFailed();
        if (target is not Actor actor || actor.Entity == default || !actor.Entity.IsAlive) return;
        ErrorAction action;
        try
        {
            action = actor.OnError(error);
        }
        catch (Exception e)
        {
            Log.ActorFailed(actor.Shard.Node.Logger, e, "OnError", actor);
            action = ErrorAction.Resume;
        }

        var entity = actor.Entity;
        if (action != ErrorAction.Reload || entity.Has<UnloadRequested>() || entity.Has<Unloading>() || entity.Has<Removing>()) return;
        entity.Add<UnloadRequested>();
        entity.Add<Fenced>(); // nothing is saved: the state may be half-changed
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Expired() => Shard.Current?.CountExpired();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogUnobserved(ILogger logger, Exception e, object target, in FrameHeader header) =>
        Log.MessageFailed(logger, e, header.Kind, header.MessageId, target);

    /// <summary>
    /// Invoke the next message of an actor: its origin is the turn's caller and, being a client session, one the actor
    /// knows (<see cref="ActorSessions"/>).
    /// </summary>
    public static System.Threading.Tasks.ValueTask Invoke(Actor actor, in Envelope envelope)
    {
        var header = FrameHeader.Read(envelope.Frame.Frame);
        actor.TurnCaller = default;
        if ((header.Flags & FrameFlags.HasOrigin) != 0)
        {
            actor.TurnCaller = envelope.Frame.Origin;
            ActorSessions.Record(actor, actor.TurnCaller);
        }

        if (header.MessageId == TypeId<SessionResumed>.Value) return Resume(actor, in envelope);
        var dispatcher = actor.HandlerType.Handlers.Of(header.MessageId)!.Dispatcher; // validated by Route
        actor.TurnChain = ChainOf(actor.Shard.World, envelope.Frame); // cleared when the turn ends
        return Invoke(dispatcher, actor, envelope.Frame, envelope.Sink, actor.Shard.Node.Logger, actor.TurnChain);
    }

    /// <summary>
    /// A client resumes a session at this actor (asked by the client-facing node): answered by the runtime, then
    /// delivered to the actor's handler, if any, as a one-way message from the new session.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static System.Threading.Tasks.ValueTask Resume(Actor actor, in Envelope envelope)
    {
        var frame = envelope.Frame;
        var resumed = NinoDeserializer.Deserialize<SessionResumed>(frame.Payload);
        bool accepted = ActorSessions.Resume(actor, in resumed);
        if (frame.CallId != 0 && envelope.Sink is { } sink)
        {
            if (accepted) sink.SendOk(frame.CallId);
            else sink.SendError(frame.CallId, RpcStatus.Forbidden);
        }

        if (accepted && actor.HandlerType.Handlers.Of(TypeId<SessionResumed>.Value) is { } handler)
            return Invoke(handler.Dispatcher, actor, frame, null, actor.Shard.Node.Logger, actor.TurnChain = ChainOf(actor.Shard.World, frame));
        frame.Dispose();
        return default;
    }

    /// <summary>
    /// Keep an unfinished service call until it completes: it becomes a <see cref="ServiceCall"/> entity whose
    /// <see cref="Turn"/> ends like an actor's (its state machine returns to its pool then).
    /// </summary>
    private static void Track(World world, System.Threading.Tasks.ValueTask task)
    {
        if (!task.IsCompleted) TurnWatcher.Begin(world, world.CreateEntity(new ServiceCall()), task);
        else task.GetAwaiter().GetResult(); // completed while being dispatched: release a pooled source (errors were replied)
    }

    /// <summary>Refuse a call or event that found its shard or mailbox full (calls get <see cref="RpcStatus.Overloaded"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Overloaded(FrameWriter frame, IReplySink? sink) => Fail(frame, sink, RpcStatus.Overloaded);

    /// <summary>
    /// Answer a request with an error status, status-only unless a <paramref name="message"/> is given (one-way messages
    /// are dropped). Disposes the frame.
    /// </summary>
    internal static void Fail(FrameWriter frame, IReplySink? sink, RpcStatus status, string? message = null)
    {
        bool request = FrameHeader.Read(frame.Frame).IsRequest;
        uint callId = frame.CallId;
        frame.Dispose();
        if (sink != null && request)
            sink.SendError(callId, status, message);
    }

    public static ILogger Logger(this World world) => world.Shard().Node.Logger;
}
