using System;
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
    /// Route a received frame (call or event): services run immediately; actor messages go to the owner's mailbox
    /// (activating it on first use) or are forwarded when another node owns the key. Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    public static void Route(World world, FrameWriter frame, IReplySink? sink)
    {
        var node = world.Shard().Node;
        var header = FrameHeader.Read(frame.Frame);
        if (header.Kind == FrameKind.Event)
        {
            RouteEvent(world, node, frame, in header);
            return;
        }

        uint requestId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (!RpcRegistry.TryGetMethod(header.MethodId, out var method))
        {
            Reject(frame, sink, requestId, RpcStatus.UnknownMethod, "Unknown method");
            return;
        }

        var contract = method.Owner;
        if (contract.Kind == RpcContractKind.Actor && !OwnsHere(world, node, contract, header.Key))
        {
            node.Forward(frame, sink);
            return;
        }

        var impl = node.ImplementationOf(contract);
        if (impl == null)
            Reject(frame, sink, requestId, RpcStatus.NotHosted, contract.ToString());
        else if (impl.Kind == RpcContractKind.Actor)
            Deliver(world, ActorOf(world, impl, header.Key), new Envelope(frame, sink));
        else
            Track(world, Invoke(contract.Dispatcher, ServiceOf(world, impl), frame, sink, world.Logger()));
    }

    /// <summary>An event for one implementation: the service instance, or the activation of the event key.</summary>
    private static void RouteEvent(World world, Node node, FrameWriter frame, in FrameHeader header)
    {
        var impl = node.ImplementationById(header.RequestId);
        var handler = impl?.HandlerOf(header.MethodId);
        if (handler == null || (impl!.Kind == RpcContractKind.Actor && !OwnsHere(world, node, impl.Id, header.Key)))
            node.ForwardEvent(frame);
        else if (impl.Kind == RpcContractKind.Actor)
            Deliver(world, ActorOf(world, impl, header.Key), new Envelope(frame, null));
        else
            Track(world, Invoke(handler.Dispatcher, ServiceOf(world, impl), frame, null, world.Logger()));
    }

    private static bool OwnsHere(World world, Node node, RpcContractInfo contract, long key) =>
        node.Placement.IsLocalOwner(contract, key) && !world.GetSingleton<ShardActivity>().Stopping;

    private static bool OwnsHere(World world, Node node, uint implementationId, long key) =>
        node.Placement.IsLocalOwner(implementationId, key) && !world.GetSingleton<ShardActivity>().Stopping;

    private static object ServiceOf(World world, ImplementationInfo impl) =>
        world.GetSingleton<ServiceDirectory>().Services[impl.Id];

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
    /// <see cref="Actor.OnActivateAsync"/> as its first turn: mail waits until it completes.
    /// </summary>
    private static Entity Activate(World world, ImplementationInfo impl, ActorId id)
    {
        var actor = (Actor)impl.Factory();
        actor.Implementation = impl;
        actor.Key = id.Key;
        actor.Shard = world.Shard();
        var entity = world.CreateEntity(new ActorLink(actor), new ActorIdentity(id), new Mailbox(new()));
        actor.Entity = entity;
        actor.InitializeEntity(entity);
        entity.Add<Turn>() = new Turn(Start(static a => a.OnActivateAsync(), actor), TurnKind.Activation);
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
    /// Deserialize and invoke a call or event on <paramref name="target"/> through a generated dispatcher. Returns the
    /// handler's task (default when it completed synchronously). A synchronous failure is replied to the caller, or
    /// logged when nobody waits for a reply (one-way call, event). Disposes <paramref name="frame"/>.
    /// </summary>
    public static System.Threading.Tasks.ValueTask Invoke(RpcDispatcher dispatcher, object target, FrameWriter frame,
        IReplySink? sink, ILogger logger)
    {
        try
        {
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

            var context = new CallContext(sink, requestId, header.MethodId, header.Key, caller);
            var previous = CallContext.Enter(in context);
            try
            {
                var reader = new Reader(payload);
                var task = dispatcher(target, header.MethodId, ref reader, in context);
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
    private static void LogUnobserved(ILogger logger, Exception e, object target, in FrameHeader header) =>
        logger.LogError(e, "{Kind} {Id:x8} failed in {Target}", header.Kind, header.MethodId, target);

    /// <summary>Invoke the next message of an actor: a call (dispatcher of its contract) or an event (of its handler).</summary>
    public static System.Threading.Tasks.ValueTask Invoke(Actor actor, in Envelope envelope)
    {
        var header = FrameHeader.Read(envelope.Frame.Frame);
        RpcDispatcher dispatcher;
        if (header.Kind == FrameKind.Event)
        {
            dispatcher = actor.Implementation.HandlerOf(header.MethodId)!.Dispatcher; // validated by RouteEvent
        }
        else
        {
            RpcRegistry.TryGetMethod(header.MethodId, out var method); // validated by Route
            dispatcher = method.Owner.Dispatcher;
        }

        return Invoke(dispatcher, actor, envelope.Frame, envelope.Sink, actor.Shard.Node.Logger);
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
