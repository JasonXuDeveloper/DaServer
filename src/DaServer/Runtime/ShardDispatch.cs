using System;
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
    /// Route a received frame: services run immediately; actor messages go to the owner's mailbox (activating it on
    /// first use) or are forwarded when another node owns the key. Takes ownership of <paramref name="frame"/>.
    /// </summary>
    public static void Route(World world, FrameWriter frame, IReplySink? sink)
    {
        var shard = world.Shard();
        var header = FrameHeader.Read(frame.Frame);
        uint requestId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (!RpcRegistry.TryGetMethod(header.MethodId, out var method))
        {
            Reject(frame, sink, requestId, RpcStatus.UnknownMethod, "Unknown method");
            return;
        }

        var contract = method.Owner;
        if (contract.Kind != RpcContractKind.Actor)
        {
            if (world.GetSingleton<ServiceDirectory>().Services.TryGetValue(contract.Id, out var service))
                Track(world, Invoke(contract, service, frame, sink));
            else
                Reject(frame, sink, requestId, RpcStatus.NotHosted, contract.ToString());
            return;
        }

        if (!shard.Node.Placement.IsLocalOwner(contract, header.Key) || world.GetSingleton<ShardActivity>().Stopping)
        {
            shard.Node.Forward(frame, sink);
            return;
        }

        var impl = shard.Node.ImplementationOf(contract);
        if (impl == null)
        {
            Reject(frame, sink, requestId, RpcStatus.NotHosted, contract.ToString());
            return;
        }

        var id = new ActorId(impl.Id, header.Key);
        var actors = world.GetSingleton<ActorDirectory>().Actors;
        if (!actors.TryGetValue(id, out var entity))
            actors[id] = entity = Activate(world, impl, id);
        Deliver(world, entity, new Envelope(frame, sink));
    }

    /// <summary>Queue a message for an actor and mark it ready unless it is busy (a running turn reschedules it).</summary>
    public static void Deliver(World world, Entity actor, in Envelope envelope)
    {
        ref var mailbox = ref actor.Get<Mailbox>();
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
    /// Deserialize and invoke a call on <paramref name="target"/> through the generated dispatcher. Returns the
    /// handler's task (default when it completed synchronously). Disposes <paramref name="frame"/>.
    /// </summary>
    public static System.Threading.Tasks.ValueTask Invoke(RpcContractInfo contract, object target, FrameWriter frame,
        IReplySink? sink)
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
                var task = contract.Dispatcher(target, header.MethodId, ref reader, in context);
                if (!task.IsCompleted) return task;
                task.GetAwaiter().GetResult();
                return default;
            }
            catch (Exception e)
            {
                context.ReplyException(e);
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

    /// <summary>Invoke the next message of an actor (contract resolved from the frame's method id).</summary>
    public static System.Threading.Tasks.ValueTask Invoke(Actor actor, in Envelope envelope)
    {
        var header = FrameHeader.Read(envelope.Frame.Frame);
        RpcRegistry.TryGetMethod(header.MethodId, out var method); // validated by Route
        return Invoke(method.Owner, actor, envelope.Frame, envelope.Sink);
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
