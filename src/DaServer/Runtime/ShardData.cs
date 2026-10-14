using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Rpc;
using EasyEcs;

namespace DaServer.Runtime;

// The shard runtime is ECS like the business code: every actor activation is an entity, its mailbox and running turn
// are components, the shard's directories and queues are singletons, and runtime systems (Systems/) each do one job.

/// <summary>
/// System group updated every time the shard wakes up (messages, completions, timers), independently of the tick:
/// message latency does not depend on the tick rate. User systems that must react to input immediately may join it;
/// simulation systems stay in the default group (fixed tick).
/// </summary>
public struct ShardMessages : ISystemGroup;

/// <summary>A message for an actor or service: the frame and where its response goes.</summary>
internal readonly record struct Envelope(FrameWriter Frame, IReplySink? Sink);

internal readonly record struct ActorId(uint ImplementationId, long Key);

// ---------------------------------------------------------------- actor components

/// <summary>Identity of an activation (directory key).</summary>
internal readonly record struct ActorIdentity(ActorId Id) : IComponent;

/// <summary>
/// Messages waiting for the actor. An actor runs one message (turn) at a time: the next starts when the previous
/// completed, including its awaits, so actor state never interleaves between calls.
/// </summary>
internal struct Mailbox(Queue<Envelope> pending) : IComponent
{
    public readonly Queue<Envelope> Pending = pending;

    /// <summary>Already in <see cref="ReadyActors"/> (each actor is queued at most once).</summary>
    public bool Scheduled;
}

internal enum TurnKind : byte
{
    Call,
    Activation,
    Deactivation,
}

/// <summary>Present while the actor runs an asynchronous turn; the actor receives no other message meanwhile.</summary>
internal readonly record struct Turn(ValueTask Task, TurnKind Kind) : IComponent;

/// <summary>Tag: the actor is being deactivated; its remaining mail is forwarded to the new owner afterwards.</summary>
internal struct Deactivating : IComponent;

// ---------------------------------------------------------------- shard singletons

/// <summary>Activations of this shard by id.</summary>
internal readonly record struct ActorDirectory(Dictionary<ActorId, Entity> Actors) : ISingletonComponent;

/// <summary>Actors with mail and no running turn, in arrival order: the only actors the mailbox system visits.</summary>
internal readonly record struct ReadyActors(Queue<Entity> Queue) : ISingletonComponent;

/// <summary>This shard's service instances by implementation id.</summary>
internal readonly record struct ServiceDirectory(Dictionary<uint, object> Services) : ISingletonComponent;

/// <summary>Asynchronous service calls still running (services are stateless, so their calls may overlap).</summary>
internal readonly record struct ServiceTasks(List<ValueTask> Tasks) : ISingletonComponent;

internal readonly record struct TimerEntry(Action<object?> Callback, object? State);

/// <summary>Callbacks due at a <see cref="System.Diagnostics.Stopwatch"/> timestamp.</summary>
internal readonly record struct ShardTimers(PriorityQueue<TimerEntry, long> Queue) : ISingletonComponent;

/// <summary>Per-wake bookkeeping shared by the runtime systems and the shard loop.</summary>
internal struct ShardActivity : ISingletonComponent
{
    /// <summary>Work items and messages handled during the current wake (0 = the shard may sleep).</summary>
    public int Processed;

    /// <summary>Ownership may have changed (membership view or shutdown): deactivate actors not owned here.</summary>
    public bool Rebalance;

    /// <summary>The shard is stopping: every actor is deactivated, no new activation starts.</summary>
    public bool Stopping;

    /// <summary>
    /// Deactivate every current activation once (the cluster declared this node Dead and moved its keys); cleared when
    /// none is left busy. New activations are allowed: they are legitimate once the node is Up again.
    /// </summary>
    public bool RetireAll;
}
