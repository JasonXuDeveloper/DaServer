namespace DaServer.Runtime;

/// <summary>
/// Operations shared by the runtime systems (routing a frame, delivering and invoking a message). Static and
/// stateless: everything they touch lives in the world. A handler runs to completion (ADR-081): one message, then the
/// next; nothing is suspended, so there are no turns.
/// </summary>
internal static partial class ShardDispatch
{
    /// <summary>
    /// Route a received message (one-way or request): services run immediately; actor messages run on the live actor,
    /// wait in the shard's mail while it is not live (loading it on first use), or are forwarded when another node owns
    /// the key. Takes ownership of <paramref name="frame"/>.
    /// </summary>
    public static void Route(World world, FrameWriter frame, IReplySink? sink)
    {
        var node = world.Shard.Node;
        var header = FrameHeader.Read(frame.Frame);
        var handlerType = node.HandlerTypeById(header.Target);
        if (header.MessageId == TypeId<SessionResumed>.Value && handlerType is { IsActor: true })
        {
            RouteSession(world, handlerType, frame, sink, in header, RpcStatus.Forbidden);
            return;
        }

        var handler = handlerType?.Handlers.Of(header.MessageId);
        if (handler == null)
        {
            if (handlerType is { IsActor: true } && Handoff.TryReceive(world, handlerType, frame, in header)) return;
            // A close reaches every granted actor, handled or not: the runtime forgets the session.
            if (header.MessageId == TypeId<SessionClosed>.Value && handlerType is { IsActor: true })
            {
                RouteSession(world, handlerType, frame, sink, in header, RpcStatus.NotHosted);
                return;
            }

            // A handler type that does not handle the message (a wrong id type) fails; one not hosted here (stale view
            // at the sender) is found on its node.
            if (handlerType != null) Fail(frame, sink, RpcStatus.NotHosted);
            else node.Forward(frame, sink);
            return;
        }

        if (!handlerType!.IsActor)
        {
            InvokeService(world, handlerType, handler, frame, sink);
            return;
        }

        var address = new ActorAddress(handlerType.Id, header.Key);
        if (TryGetLoaded(world, address, out var existing))
            Deliver(world, existing, address, new Envelope(frame, sink)); // even while leaving: its mail moves in order
        else if (!OwnsHere(world, node, handlerType.Id, header.Key))
            node.Forward(frame, sink);
        else
            Load(world, handlerType, address, new Envelope(frame, sink), Handoff.PreviousOwner(world, handlerType.Id, header.Key));
    }

    private static bool OwnsHere(World world, Node node, uint handlerTypeId, long key) =>
        node.IsLocalOwner(handlerTypeId, key) && !world.ShardActivity.Stopping;

    /// <summary>Whether this shard takes actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>) now (it owns it and is not stopping).</summary>
    internal static bool Owns(World world, uint handlerTypeId, long key) => OwnsHere(world, world.Shard.Node, handlerTypeId, key);

    /// <summary>A loaded actor of this shard, including one leaving (new mail queues behind its mail).</summary>
    internal static bool TryGetLoaded(World world, ActorAddress address, out Entity actor) =>
        world.ActorKeyIndex.TryFind(ActorKey.Of(address), out actor);

    /// <summary>
    /// Load actor <paramref name="address"/> for its first message: its entity holds its identity and waits for its
    /// record (<see cref="Loading"/>) while the shard holds the message. <paramref name="previousOwner"/>: the node that
    /// may still hold it (<see cref="Handoff.PreviousOwner"/>), whose record the load waits for.
    /// </summary>
    internal static void Load(World world, HandlerTypeInfo handlerType, ActorAddress address, in Envelope envelope, ulong? previousOwner)
    {
        var entity = Create(world, handlerType, address);
        if (previousOwner == null && !world.ShardStore.Enabled)
        {
            // Nothing to wait for: live now, and the message runs at once (it never enters the mailbox).
            Handoff.ForgetHanded(world, address.HandlerTypeId, address.Key);
            if (BeginLive(world, entity, restored: false))
            {
                Deliver(world, entity, address, in envelope);
                return;
            }

            Queue(world, entity, address, handlerType, in envelope);
            entity.SetRemoving(RpcStatus.Error); // its mail fails
            return;
        }

        Queue(world, entity, address, handlerType, in envelope);
        if (previousOwner is { } from) Handoff.Await(world, entity, from);
        else BeginLoad(world, entity, address);
    }

    /// <summary>An actor's entity, not live: <see cref="Handoff"/> restores what its previous owner sent first.</summary>
    internal static Entity Adopt(World world, HandlerTypeInfo handlerType, long key) =>
        Create(world, handlerType, new ActorAddress(handlerType.Id, key));

    private static Entity Create(World world, HandlerTypeInfo handlerType, ActorAddress address)
    {
        var entity = world.CreateEntity(ActorKey.Of(address), new LastMail(world.ShardActivity.NowMs), new Loading());
        int idle = handlerType.Settings.UnloadAfterIdleSeconds;
        if (idle > 0) entity.SetUnloadWhenIdle(idle * 1000L);
        return entity;
    }

    /// <summary>The actor's record comes from the store (when it has one), then it becomes live.</summary>
    internal static void BeginLoad(World world, Entity entity, ActorAddress address)
    {
        Handoff.ForgetHanded(world, address.HandlerTypeId, address.Key); // the key is here again
        if (world.ShardStore.Enabled) // LoadSystem restores it, then BeginLive
            entity.SetAwaitingRecord(world.ShardActivity.NowMs, 0);
        else if (!BeginLive(world, entity, restored: false)) entity.SetRemoving(RpcStatus.Error);
    }

    /// <summary>
    /// The actor's record is restored (or it has none): it is live from now on (systems see it), its first mail is
    /// <see cref="Loaded"/> (<paramref name="restored"/>) or <see cref="Created"/>, then its waiting mail runs. False when
    /// that handler threw: the caller decides where the actor goes (its waiting mail fails, or a moved record is written).
    /// </summary>
    internal static bool BeginLive(World world, Entity entity, bool restored)
    {
        entity.RemoveLoading();
        var key = entity.ActorKey;
        var handlerType = world.Shard.Node.HandlerTypeById(key.HandlerTypeId)!;
        var lifecycle = handlerType.Handlers.Of(restored ? TypeId<Loaded>.Value : TypeId<Created>.Value);
        if (lifecycle == null) return true;
        var context = new Context(world.Shard, key.Address, entity, default, null, 0, 0);
        try
        {
            lifecycle.Dispatch(in context, restored ? LifecycleMail<Loaded>.Payload : LifecycleMail<Created>.Payload);
            return true;
        }
        catch (Exception e)
        {
            Log.ActorFailed(world.Logger, e, restored ? "Loaded" : "Created", handlerType);
            world.Shard.CountFailed();
            return false;
        }
    }

    /// <summary>The leaving actor's last mail, <see cref="Unloading"/> (when it handles it), before its record is written or handed on.</summary>
    internal static void Unload(World world, Entity actor, ActorAddress address)
    {
        var handlerType = world.Shard.Node.HandlerTypeById(address.HandlerTypeId)!;
        if (handlerType.Handlers.Of(TypeId<Unloading>.Value) is not { } handler) return;
        var context = new Context(world.Shard, address, actor, default, null, 0, 0);
        try
        {
            handler.Dispatch(in context, LifecycleMail<Unloading>.Payload);
        }
        catch (Exception e)
        {
            Log.ActorFailed(world.Logger, e, "Unloading", handlerType);
            world.Shard.CountFailed();
        }
    }

    /// <summary>
    /// A message for a loaded actor: it runs now when the actor is live and has no mail waiting, else it waits in the
    /// shard's mail (in order). A full queue (<see cref="ActorSettings.MailboxCapacity"/>) refuses the message: one actor
    /// that cannot take mail cannot hold the shard's memory.
    /// </summary>
    public static void Deliver(World world, Entity actor, ActorAddress address, in Envelope envelope)
    {
        if (IsLive(actor) && !actor.IsReady)
        {
            Invoke(world, actor, address, in envelope);
            return;
        }

        Queue(world, actor, address, world.Shard.Node.HandlerTypeById(address.HandlerTypeId)!, in envelope);
    }

    /// <summary>Whether the actor takes mail now (not loading, leaving or going).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsLive(Entity actor) =>
        !actor.IsLoading && !actor.IsUnloadRequested && !actor.IsLeaving && !actor.HasRemoving;

    private static void Queue(World world, Entity actor, ActorAddress address, HandlerTypeInfo handlerType, in Envelope envelope)
    {
        ref var mailbox = ref actor.GetOrAddEnvelope();
        int capacity = handlerType.Settings.MailboxCapacity;
        if (mailbox.Count >= (capacity > 0 ? capacity : world.Shard.Node.Options.ActorMailboxCapacity))
        {
            world.Shard.CountMailboxFull();
            Overloaded(envelope.Frame, envelope.Sink);
            return;
        }

        mailbox.Add(in envelope);
        actor.SetReady(); // `mailbox` is invalid from here on
    }

    /// <summary>
    /// Run a message on a live actor: its origin, being a client session, is one the actor knows
    /// (<see cref="ActorSessions"/>); runtime messages about sessions are applied first. Disposes the frame.
    /// </summary>
    public static void Invoke(World world, Entity actor, ActorAddress address, in Envelope envelope)
    {
        var frame = envelope.Frame;
        var header = FrameHeader.Read(frame.Frame);
        var origin = (header.Flags & FrameFlags.HasOrigin) != 0 ? frame.Origin : default;
        if (origin.IsValid) ActorSessions.Record(world, actor, origin);
        actor.LastMail.Ms = world.ShardActivity.NowMs;

        if (header.MessageId == TypeId<SessionResumed>.Value)
        {
            Resume(world, actor, address, in envelope);
            return;
        }

        if (header.MessageId == TypeId<SessionClosed>.Value && !ActorSessions.Closed(world, actor, frame.Payload))
        {
            Fail(frame, envelope.Sink, RpcStatus.NotHosted); // a close of a session resumed since: stale
            return;
        }

        var handlerType = world.Shard.Node.HandlerTypeById(address.HandlerTypeId)!;
        var handler = handlerType.Handlers.Of(header.MessageId);
        if (handler == null)
        {
            if (frame.CallId != 0 && envelope.Sink is { } closed) closed.SendOk(frame.CallId);
            frame.Dispose(); // a close the actor does not handle: forgotten above
            return;
        }

        Run(world, handlerType, handler, actor, address, origin, frame, envelope.Sink);
    }

    /// <summary>A service's message runs at once: a service has no entity and no mail of its own.</summary>
    private static void InvokeService(World world, HandlerTypeInfo handlerType, ActorHandler handler, FrameWriter frame, IReplySink? sink)
    {
        var header = FrameHeader.Read(frame.Frame);
        var origin = (header.Flags & FrameFlags.HasOrigin) != 0 ? frame.Origin : default;
        Run(world, handlerType, handler, default, new ActorAddress(handlerType.Id, 0), origin, frame, sink);
    }

    /// <summary>
    /// Call a handler through its generated dispatch. A failure is replied to the caller, or logged when nobody waits for
    /// a reply, and applies the actor type's <see cref="ActorSettings.OnError"/>. Disposes <paramref name="frame"/>.
    /// </summary>
    private static void Run(World world, HandlerTypeInfo handlerType, ActorHandler handler, Entity actor, ActorAddress address,
        Origin origin, FrameWriter frame, IReplySink? sink)
    {
        try
        {
            if (frame.Deadline != 0 && Platform.Milliseconds > frame.Deadline)
            {
                Expired(); // the caller gave up while the request waited: running it would be wasted (and late) work
                return;
            }

            var context = new Context(world.Shard, address, actor, origin, sink, frame.CallId, frame.Deadline);
            var span = Tracing.Start(frame, handlerType, handler, address.Key, out var outer);
            Exception? error = null;
            try
            {
                handler.Dispatch(in context, frame.Payload);
            }
            catch (Exception e)
            {
                error = e;
                if (context.ExpectsReply) context.Call.ReplyException(e);
                else Log.MessageFailed(world.Logger, e, FrameHeader.Read(frame.Frame).Kind, handler.MessageId, handlerType);
                // An answer's handler that failed cannot reply to the request it was answering: its caller learns now,
                // not at its timeout (a no-op when the handler replied before it threw).
                if (handler.IsAnswer) Replies.Fail(world, Answers.ForwardOf(frame.Payload), RpcStatus.Error);
                Failed(world, actor, handlerType, e);
            }
            finally
            {
                if (span != null) Tracing.Dispatched(span, outer, error);
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// A handler of <paramref name="actor"/> failed: count it, and apply its type's <see cref="ActorSettings.OnError"/>
    /// (C4). Reload removes the actor without saving; its mail loads it again.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Failed(World world, Entity actor, HandlerTypeInfo handlerType, Exception error)
    {
        world.Shard.CountFailed();
        if (handlerType.Settings.OnError != ErrorAction.Reload || !actor.IsAlive) return;
        // Also when an unload was asked for meanwhile: nothing is saved or handed on, the state may be half-changed.
        if (actor.IsFenced || actor.IsLeaving || actor.HasRemoving) return;
        actor.Set(new UnloadRequested(), new Fenced());
    }

    /// <summary>The bytes of a lifecycle message (<see cref="Created"/>, <see cref="Loaded"/>, <see cref="Unloading"/>), written once.</summary>
    internal static class LifecycleMail<T> where T : struct
    {
        public static readonly byte[] Payload = NinoSerializer.Serialize(default(T));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Expired() => Shard.Current?.CountExpired();

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
}
