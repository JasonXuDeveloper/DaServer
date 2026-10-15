using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DaServer.Buffers;
using DaServer.Protocol;
using DaServer.Rpc;
using EasyEcs;
using Microsoft.Extensions.Logging;
using Nino.Core;

namespace DaServer.Runtime;

/// <summary>
/// Operations shared by the runtime systems (routing a frame, invoking a target, retiring an activation). Static and
/// stateless: everything they touch lives in the world.
/// </summary>
internal static class ShardDispatch
{
    /// <summary>Serialized size of a <see cref="SessionRef"/> caller prefix.</summary>
    internal static readonly int SessionRefSize = NinoSerializer.Serialize(default(SessionRef)).Length;

    /// <summary>
    /// Route a received message (one-way or request): services run immediately; actor messages go to the owner's
    /// mailbox (activating it on first use) or are forwarded when another node owns the key. Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    public static void Route(World world, FrameWriter frame, IReplySink? sink)
    {
        var node = world.Shard().Node;
        var header = FrameHeader.Read(frame.Frame);
        bool request = header.Kind == FrameKind.Request;
        var impl = request
            ? node.RequestHandler(header.MessageId, (header.Flags & FrameFlags.ToActor) != 0)
            : node.ImplementationById(header.RequestId);
        var handler = impl?.Handlers.Of(header.MessageId);
        if (handler == null)
        {
            // Not hosted here (stale view at the sender): an actor message finds its owner, a service request fails.
            if (request && (header.Flags & FrameFlags.ToActor) == 0)
                Reject(frame, sink, header.RequestId, RpcStatus.NotHosted, "No service handles the message here");
            else
                node.Forward(frame, sink);
            return;
        }

        if (!impl!.IsActor)
            Track(world, Invoke(handler.Dispatcher, ServiceOf(world, impl), frame, sink, world.Logger()));
        else if (TryGetActivation(world, impl.Id, header.Key, out var existing))
            Deliver(world, existing, new Envelope(frame, sink)); // even while deactivating: its mail moves in order
        else if (!OwnsHere(world, node, impl.Id, header.Key))
            node.Forward(frame, sink);
        else
            Deliver(world, ActorOf(world, impl, header.Key), new Envelope(frame, sink));
    }

    private static bool OwnsHere(World world, Node node, uint implementationId, long key) =>
        node.Placement.IsLocalOwner(implementationId, key) && !world.GetSingleton<ShardActivity>().Stopping;

    private static object ServiceOf(World world, ImplementationInfo impl) =>
        world.GetSingleton<ServiceDirectory>().Services[impl.Id];

    /// <summary>An activation of this shard, including one being deactivated (new mail queues behind its mail).</summary>
    private static bool TryGetActivation(World world, uint implementationId, long key, out Entity actor) =>
        world.GetSingleton<ActorDirectory>().Actors.TryGetValue(new ActorId(implementationId, key), out actor);

    /// <summary>The activation of (<paramref name="impl"/>, <paramref name="key"/>) on this shard, activated on first use.</summary>
    private static Entity ActorOf(World world, ImplementationInfo impl, long key)
    {
        var id = new ActorId(impl.Id, key);
        var actors = world.GetSingleton<ActorDirectory>().Actors;
        if (!actors.TryGetValue(id, out var entity))
            actors[id] = entity = Activate(world, impl, id);
        return entity;
    }

    /// <summary>
    /// Queue a message for an actor and mark it ready unless it is busy (a running turn reschedules it). A full
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
        Schedule(world, actor, ref mailbox);
    }

    /// <summary>Put an idle actor with mail into <see cref="ReadyActors"/> (once).</summary>
    public static void Schedule(World world, Entity actor, ref Mailbox mailbox)
    {
        if (mailbox.Scheduled || mailbox.Pending.Count == 0 || actor.Has<Turn>()) return;
        mailbox.Scheduled = true;
        world.GetSingleton<ReadyActors>().Queue.Enqueue(actor);
    }

    /// <summary>
    /// Create the activation's entity (link, identity, mailbox, the actor's own components) and start
    /// <see cref="Actor.OnLoad"/> as its first turn: mail waits until it completes.
    /// </summary>
    private static Entity Activate(World world, ImplementationInfo impl, ActorId id)
    {
        var actor = (Actor)impl.Factory();
        actor.Implementation = impl;
        actor.Key = id.Key;
        actor.Shard = world.Shard();
        var entity = world.CreateEntity(new ActorLink(actor), new ActorIdentity(id),
            new Mailbox(new()) { LastActive = Environment.TickCount64 });
        actor.Entity = entity;
        var idle = actor.UnloadAfterIdle;
        if (idle > TimeSpan.Zero) entity.Add<UnloadWhenIdle>() = new UnloadWhenIdle((long)idle.TotalMilliseconds);
        actor.InitializeEntity(entity);
        entity.Add<Turn>() = new Turn(Start(static a => a.OnLoad(), actor), TurnKind.Activation);
        return entity;
    }

    /// <summary>
    /// Start unloading an idle actor: <see cref="Actor.OnUnload"/> runs as its last turn, then
    /// <see cref="TurnSystem"/> retires it and routes mail that arrived meanwhile again (to the new owner, or to a new
    /// activation here).
    /// </summary>
    public static void Unload(Entity entity, Actor actor)
    {
        entity.Add<Deactivating>();
        entity.Add<Turn>() = new Turn(Start(static a => a.OnUnload(), actor), TurnKind.Deactivation);
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

            var context = new CallContext(sink, requestId, header.MessageId, header.Key, caller);
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
    /// <see cref="ClientSessions"/>) and forgets a closed one.
    /// </summary>
    public static System.Threading.Tasks.ValueTask Invoke(Actor actor, in Envelope envelope)
    {
        var header = FrameHeader.Read(envelope.Frame.Frame);
        actor.TurnCaller = default;
        if (header.MessageId == MessageType<SessionClosed>.Id) ForgetSession(actor, envelope.Frame.Payload);
        else if ((header.Flags & FrameFlags.HasCaller) != 0)
        {
            var reader = new Reader(envelope.Frame.Payload);
            NinoDeserializer.Deserialize(out actor.TurnCaller, ref reader);
            RecordSession(actor.Entity, actor.TurnCaller);
        }

        var dispatcher = actor.Implementation.Handlers.Of(header.MessageId)!.Dispatcher; // validated by Route
        return Invoke(dispatcher, actor, envelope.Frame, envelope.Sink, actor.Shard.Node.Logger);
    }

    /// <summary>Remember a client session of an activation (a hash lookup; allocates once per new session).</summary>
    internal static void RecordSession(Entity actor, SessionRef session)
    {
        if (!actor.TryGet<ClientSessions>(out var sessions))
            actor.Add<ClientSessions>() = sessions = new ClientSessions(new HashSet<SessionRef>());
        sessions.Sessions.Add(session);
    }

    private static void ForgetSession(Actor actor, ReadOnlySpan<byte> payload)
    {
        if (!actor.Entity.TryGet<ClientSessions>(out var sessions)) return;
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out SessionClosed closed, ref reader);
        sessions.Sessions.Remove(closed.Session);
    }

    /// <summary>
    /// A gateway died without closing its sessions: every activation of this shard that knew one of them receives
    /// <see cref="SessionClosed"/> (when it handles it), as if the gateway had closed the session.
    /// </summary>
    internal static void CloseSessionsOf(World world, ulong gatewayNodeId)
    {
        List<SessionRef>? lost = null;
        foreach (var item in world.Query<ClientSessions, ActorLink>())
        {
            var sessions = item.Component1.Sessions;
            lost?.Clear();
            foreach (var session in sessions)
                if (session.GatewayNodeId == gatewayNodeId) (lost ??= new()).Add(session);
            if (lost == null || lost.Count == 0) continue;

            var actor = item.Component2.Actor;
            bool handles = actor.Implementation.Handlers.Of(MessageType<SessionClosed>.Id) != null;
            foreach (var session in lost)
            {
                sessions.Remove(session);
                if (!handles) continue;
                var w = MessageWriter.Write(new SessionClosed(session));
                w.SetHeader(FrameKind.Message, actor.Implementation.Id, MessageType<SessionClosed>.Id, actor.Key);
                Deliver(world, item.Entity, new Envelope(w, null));
            }
        }
    }

    /// <summary>Keep an unfinished service call until it completes (its state machine returns to its pool then).</summary>
    private static void Track(World world, System.Threading.Tasks.ValueTask task)
    {
        if (!task.IsCompleted) world.GetSingleton<ServiceTasks>().Tasks.Add(task);
    }

    /// <summary>
    /// Remove an activation: drop it from the directory, destroy its entity, and forward its remaining mail (it is
    /// routed again, now to the new owner) or fail it with <paramref name="status"/>.
    /// </summary>
    public static void Retire(World world, Entity actor, RpcStatus status = RpcStatus.Ok, string? message = null)
    {
        var pending = actor.Get<Mailbox>().Pending;
        world.GetSingleton<ActorDirectory>().Actors.Remove(actor.Get<ActorIdentity>().Id);
        actor.Destroy();

        while (pending.TryDequeue(out var envelope))
        {
            if (status == RpcStatus.Ok)
            {
                Route(world, envelope.Frame, envelope.Sink);
                continue;
            }

            var header = FrameHeader.Read(envelope.Frame.Frame);
            Reject(envelope.Frame, envelope.Sink, header.Kind == FrameKind.Request ? header.RequestId : 0, status,
                message ?? status.ToString());
        }
    }

    /// <summary>Answer a request with an error status (one-way messages are dropped). Disposes the frame.</summary>
    /// <summary>Refuse a call or event that found its shard or mailbox full (calls get <see cref="RpcStatus.Overloaded"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Overloaded(FrameWriter frame, IReplySink? sink)
    {
        var header = FrameHeader.Read(frame.Frame);
        Reject(frame, sink, header.Kind == FrameKind.Request ? header.RequestId : 0, RpcStatus.Overloaded, "Overloaded");
    }

    private static void Reject(FrameWriter frame, IReplySink? sink, uint requestId, RpcStatus status, string message)
    {
        frame.Dispose();
        if (sink == null || requestId == 0) return;
        var reply = FrameWriter.Rent();
        var nino = reply.Nino;
        NinoSerializer.Serialize(message, ref nino);
        sink.SendReply(requestId, status, reply);
    }

    public static ILogger Logger(this World world) => world.Shard().Node.Logger;
}
