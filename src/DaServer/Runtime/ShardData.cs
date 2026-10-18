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

/// <summary>A message for an actor or service: the frame and where its reply goes.</summary>
internal readonly record struct Envelope(FrameWriter Frame, IReplySink? Sink);

internal readonly record struct ActorAddress(uint HandlerTypeId, long Key);

// ---------------------------------------------------------------- actor components


/// <summary>
/// Messages waiting for the actor. An actor runs one message (turn) at a time: the next starts when the previous
/// completed, including its awaits, so actor state never interleaves between calls.
/// </summary>
internal struct Mailbox(Queue<Envelope> pending) : IComponent
{
    public readonly Queue<Envelope> Pending = pending;

    /// <summary><see cref="ShardActivity.NowMs"/> when the actor last took mail (idle unloading).</summary>
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
internal struct Turn(ValueTask task, long startedMs) : IComponent
{
    public readonly ValueTask Task = task;

    /// <summary><see cref="ShardActivity.NowMs"/> when the turn started.</summary>
    public readonly long StartedMs = startedMs;

    /// <summary>The turn outlived the call timeout and was logged (<see cref="LongTurnSystem"/>).</summary>
    public bool Reported;
}

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
/// Client sessions known to a loaded actor: those that called it or that it bound or granted. When a
/// client-facing node dies without closing its sessions, the runtime delivers <see cref="SessionClosed"/> for them. Granted:
/// the sessions it holds bound or granted now, the only ones a client may resume from (<see cref="SessionResumed"/>).
/// Superseded: sessions a client resumed from, whose late close is not delivered.
/// </summary>
internal readonly record struct ClientSessions(
    HashSet<ClientSession> Sessions, HashSet<ClientSession> Granted, HashSet<ClientSession> Superseded) : IComponent;

// ---------------------------------------------------------------- shard singletons

/// <summary>Loaded actors of this shard by id.</summary>
internal readonly record struct ActorDirectory(Dictionary<ActorAddress, Entity> Actors) : ISingletonComponent;

/// <summary>
/// What a loaded actor holds besides its components, kept for the next load on this shard: the actor object (actors and
/// services hold no fields, DAS007), the mailbox queue and the session sets. Rented on load, returned on removal, both
/// on the shard thread; each pool keeps at most <see cref="Bound"/> idle items, so churn (players logging in and out,
/// idle unloads) allocates nothing while the peak of loaded actors stays bounded memory-wise.
/// </summary>
internal readonly record struct ActorPools(
    Dictionary<uint, Stack<Actor>> Actors, Stack<Queue<Envelope>> Mailboxes, Stack<ClientSessions> Sessions) : ISingletonComponent
{
    /// <summary>Idle items each pool keeps (per actor type for actor objects).</summary>
    public const int Bound = 4096;

    /// <summary>A mailbox that grew beyond this (a backlog) is dropped rather than pooled with its large array.</summary>
    private const int MaxPooledMailbox = 64;

    public Actor RentActor(HandlerTypeInfo handlerType) =>
        Actors.TryGetValue(handlerType.Id, out var pool) && pool.TryPop(out var actor) ? actor : (Actor)handlerType.Factory();

    public Queue<Envelope> RentMailbox() => Mailboxes.TryPop(out var queue) ? queue : new Queue<Envelope>();

    public ClientSessions RentSessions() =>
        Sessions.TryPop(out var sessions) ? sessions : new ClientSessions(new HashSet<ClientSession>(), new HashSet<ClientSession>(), new HashSet<ClientSession>());

    /// <summary>Return what a removed actor held (its entity is destroyed and its mailbox drained).</summary>
    public void Return(Actor actor, Queue<Envelope> mailbox, in ClientSessions sessions, bool hasSessions)
    {
        actor.Key = 0;
        actor.Entity = default;
        actor.TurnCaller = default;
        actor.TurnChain = 0;
        if (!Actors.TryGetValue(actor.HandlerType.Id, out var actors))
            Actors[actor.HandlerType.Id] = actors = new Stack<Actor>();
        if (actors.Count < Bound) actors.Push(actor);

        if (Mailboxes.Count < Bound && mailbox.EnsureCapacity(0) <= MaxPooledMailbox) Mailboxes.Push(mailbox);

        if (hasSessions && Sessions.Count < Bound && sessions.Sessions.Count + sessions.Superseded.Count <= MaxPooledMailbox)
        {
            sessions.Sessions.Clear();
            sessions.Granted.Clear();
            sessions.Superseded.Clear();
            Sessions.Push(sessions);
        }
    }
}

/// <summary>
/// Chains of the turns this shard starts for messages that carry none (ADR-059): consecutive values from a random start,
/// so chains of different shards and nodes do not meet.
/// </summary>
internal struct CallChains : ISingletonComponent
{
    public long Last;

    public long Next() => ++Last == 0 ? ++Last : Last;
}

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

    /// <summary>
    /// Milliseconds on the shard clock (<see cref="NodeOptions.TimeProvider"/>) when the current wake started: the time
    /// of idle unloading and checkpoints, so a manual clock (tests) moves them too.
    /// </summary>
    public long NowMs;

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
