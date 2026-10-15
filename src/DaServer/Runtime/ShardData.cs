namespace DaServer.Runtime;

// The shard runtime is ECS like the business code: every actor is an entity, its mailbox and running turn
// are components, the shard's directories and queues are singletons, and runtime systems (Systems/) each do one job.

/// <summary>
/// System group updated every time the shard wakes up (messages, completions, timers), independently of the tick:
/// message latency does not depend on the tick rate. User systems that must react to input immediately may join it;
/// simulation systems stay in the default group (fixed tick).
/// </summary>
public struct ShardMessages : ISystemGroup;

/// <summary>Runtime systems run only after ownership may have changed (membership change, node Dead, shutdown).</summary>
internal struct ShardRebalance : ISystemGroup;

/// <summary>Runtime systems run a few times a second to find idle actors.</summary>
internal struct ShardIdleScan : ISystemGroup;

/// <summary>A message for an actor or service: the frame and where its response goes.</summary>
internal readonly record struct Envelope(FrameWriter Frame, IReplySink? Sink);

internal readonly record struct ActorId(uint HandlerTypeId, long Key);

// ---------------------------------------------------------------- actor components


/// <summary>
/// Messages waiting for the actor. An actor runs one message (turn) at a time: the next starts when the previous
/// completed, including its awaits, so actor state never interleaves between calls.
/// </summary>
internal struct Mailbox(Queue<Envelope> pending) : IComponent
{
    public readonly Queue<Envelope> Pending = pending;

    /// <summary><see cref="Environment.TickCount64"/> when the actor last took mail (idle unloading).</summary>
    public long LastActive;
}

/// <summary>Tag: the mailbox has mail (<see cref="MailboxSystem"/> runs it once the actor has no <see cref="Turn"/>).</summary>
internal struct Ready : IComponent;

/// <summary>The actor unloads after <see cref="Milliseconds"/> without mail (<see cref="Actor.UnloadAfterIdle"/>).</summary>
internal readonly record struct UnloadWhenIdle(long Milliseconds) : IComponent;

/// <summary>
/// An asynchronous turn is running: the actor receives no other message meanwhile. Also on the entity of a running
/// service call (<see cref="ServiceCall"/>). Its end arrives as data: <see cref="TurnEnded"/>.
/// </summary>
internal readonly record struct Turn(ValueTask Task) : IComponent;

/// <summary>Tag: the <see cref="Turn"/>'s task completed (posted by its <see cref="TurnWatcher"/>); ended by a system.</summary>
internal struct TurnEnded : IComponent;

/// <summary>Tag: the turn is <see cref="Actor.OnLoad"/>; mail waits for it.</summary>
internal struct Loading : IComponent;

/// <summary>Tag: the actor must leave this shard (not owned here, idle, stopping); it starts unloading once idle.</summary>
internal struct UnloadRequested : IComponent;

/// <summary>
/// Tag: the cluster declared this node dead and gave the actor's key to another node, so the actor is removed without
/// <see cref="Actor.OnUnload"/>: a save now could overwrite what the new owner already wrote.
/// </summary>
internal struct Fenced : IComponent;

/// <summary>Tag: the turn is <see cref="Actor.OnUnload"/>; mail arriving meanwhile is forwarded afterwards.</summary>
internal struct Unloading : IComponent;

/// <summary>
/// The loaded actor ends: its entity is destroyed and its remaining mail routed again (<see cref="RpcStatus.Ok"/>, to the
/// new owner) or failed with <see cref="Status"/>.
/// </summary>
internal readonly record struct Removing(RpcStatus Status, string? Message) : IComponent;

/// <summary>Tag: the entity is an asynchronous service call (services are stateless, so their calls may overlap).</summary>
internal struct ServiceCall : IComponent;

/// <summary>
/// Client sessions known to a loaded actor: those that called it through a gateway, or that it bound to itself. When a
/// gateway dies without closing its sessions, the runtime delivers <see cref="SessionClosed"/> for them. Superseded:
/// sessions a client resumed from (<see cref="SessionResync"/>), whose late close is not delivered.
/// </summary>
internal readonly record struct ClientSessions(HashSet<SessionRef> Sessions, HashSet<SessionRef> Superseded) : IComponent;

// ---------------------------------------------------------------- shard singletons

/// <summary>Loaded actors of this shard by id.</summary>
internal readonly record struct ActorDirectory(Dictionary<ActorId, Entity> Actors) : ISingletonComponent;

/// <summary>Idle <see cref="TurnWatcher"/>s (bounded by the turns in flight).</summary>
internal readonly record struct TurnWatchers(Stack<TurnWatcher> Pool) : ISingletonComponent;

/// <summary>This shard's service instances by handler type id.</summary>
internal readonly record struct ServiceDirectory(Dictionary<uint, object> Services) : ISingletonComponent;

/// <summary>Per-wake bookkeeping shared by the runtime systems and the shard loop.</summary>
internal struct ShardActivity : ISingletonComponent
{
    /// <summary>Work items and messages handled during the current wake (0 = the shard may sleep).</summary>
    public int Processed;

    /// <summary>Messages <see cref="MailboxSystem"/> may still run during the current wake.</summary>
    public int MailBudget;

    /// <summary>Ownership may have changed (membership view or shutdown): the shard runs <see cref="ShardRebalance"/>.</summary>
    public bool Rebalance;

    /// <summary>The shard is stopping: every actor is unloaded, no actor loads.</summary>
    public bool Stopping;

    /// <summary>
    /// Remove every loaded actor once, without <see cref="Actor.OnUnload"/> (<see cref="Fenced"/>: the cluster declared this
    /// node Dead and moved its keys). New loads are allowed: they are legitimate once the node is Up again.
    /// </summary>
    public bool DeclaredDead;
}
