namespace DaServer.Runtime;

// The shard runtime is ECS like the business code (ADR-080): every actor is an entity whose components are data (its
// identity, its lifecycle as tags, its user components); the shard's mail, asks and replies in flight are singletons;
// runtime systems (Systems/) each do one job.

/// <summary>
/// System group updated every time the shard wakes up (messages, completions, timers), independently of the tick:
/// message latency does not depend on the tick rate. User systems that must react to input immediately may join it;
/// simulation systems stay in the default group (fixed tick).
/// </summary>
public struct ShardMessages : ISystemGroup;

/// <summary>Runtime systems run only after ownership may have changed (membership change, node Dead, shutdown).</summary>
internal struct ShardRebalance : ISystemGroup;

/// <summary>Runtime systems run a few times a second to find idle actors and expired replies.</summary>
internal struct ShardIdleScan : ISystemGroup;

/// <summary>A message for an actor or service: the frame and where its reply goes.</summary>
internal readonly record struct Envelope(FrameWriter Frame, IReplySink? Sink);

internal readonly record struct ActorAddress(uint HandlerTypeId, long Key);

// ---------------------------------------------------------------- actor components

/// <summary>
/// Tag: the actor's mailbox (its <c>DynamicBuffer&lt;Envelope&gt;</c>) holds mail; <see cref="MailSystem"/> runs it once
/// the actor is live. A live actor without waiting mail runs a message at once on arrival.
/// </summary>
internal struct Ready : IComponent;

/// <summary><see cref="ShardActivity.NowMs"/> when the actor last took mail (idle unloading).</summary>
internal record struct LastMail(long Ms) : IComponent;

/// <summary>The actor unloads after <see cref="Milliseconds"/> without mail (its <see cref="ActorSettings.UnloadAfterIdleSeconds"/>).</summary>
internal readonly record struct UnloadWhenIdle(long Milliseconds) : IComponent;

/// <summary>Tag: the actor's record is being restored (from the store or its previous owner); its mail waits.</summary>
internal struct Loading : IComponent;

/// <summary>Tag: the actor must leave this shard (not owned here, idle, stopping, reloading after a failure).</summary>
internal struct UnloadRequested : IComponent;

/// <summary>
/// Tag: the cluster declared this node dead and gave the actor's key to another node, so the actor is removed without
/// its <see cref="Unloading"/> mail: a save now could overwrite what the new owner already wrote.
/// </summary>
internal struct Fenced : IComponent;

/// <summary>Tag: the actor got its <see cref="Unloading"/> mail and leaves (its record is written or handed on first).</summary>
internal struct Leaving : IComponent;

/// <summary>
/// The loaded actor ends: its entity is destroyed and its remaining mail routed again (<see cref="RpcStatus.Ok"/>, to the
/// new owner) or failed with <see cref="Status"/>.
/// </summary>
internal readonly record struct Removing(RpcStatus Status) : IComponent;

/// <summary>
/// Client sessions known to a loaded actor: those that called it or that it bound or granted. When a
/// client-facing node dies without closing its sessions, the runtime delivers <see cref="SessionClosed"/> for them. Granted:
/// the sessions it holds bound or granted now, the only ones a client may resume from (<see cref="SessionResumed"/>).
/// Superseded: sessions a client resumed from, whose late close is not delivered.
/// </summary>
internal readonly record struct ClientSessions(
    HashSet<ClientSession> Sessions, HashSet<ClientSession> Granted, HashSet<ClientSession> Superseded) : IComponent;

// ---------------------------------------------------------------- shard singletons

/// <summary>
/// Room for the mail an actor is handed while it is removed (it goes on to the new owner, or fails): one list per
/// shard, reused.
/// </summary>
internal readonly record struct MailScratch(List<Envelope> Items) : ISingletonComponent;

/// <summary>Idle session sets, rented when an actor first knows a session and returned when it is removed (bounded).</summary>
internal readonly record struct SessionPool(Stack<ClientSessions> Idle) : ISingletonComponent
{
    private const int Bound = 4096;
    private const int MaxPooled = 64;

    public ClientSessions Rent() =>
        Idle.TryPop(out var sessions) ? sessions : new ClientSessions(new HashSet<ClientSession>(), new HashSet<ClientSession>(), new HashSet<ClientSession>());

    public void Return(in ClientSessions sessions)
    {
        if (Idle.Count >= Bound || sessions.Sessions.Count + sessions.Superseded.Count > MaxPooled) return;
        sessions.Sessions.Clear();
        sessions.Granted.Clear();
        sessions.Superseded.Clear();
        Idle.Push(sessions);
    }
}

/// <summary>Per-wake bookkeeping shared by the runtime systems and the shard loop.</summary>
internal struct ShardActivity : ISingletonComponent
{
    /// <summary>Work items and messages handled during the current wake (0 = the shard may sleep).</summary>
    public int Processed;

    /// <summary>Messages <see cref="MailSystem"/> may still run during the current wake.</summary>
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
    /// Remove every loaded actor once, without its <see cref="Unloading"/> mail (<see cref="Fenced"/>: the cluster declared this
    /// node Dead and moved its keys). New loads are allowed: they are legitimate once the node is Up again.
    /// </summary>
    public bool DeclaredDead;
}
