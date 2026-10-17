namespace DaServer.Runtime;

/// <summary>Configuration of a <see cref="Node"/> (bound from the "DaServer" configuration section by the host).</summary>
public sealed class NodeOptions
{
    /// <summary>Human-readable node name (logs, thread names).</summary>
    public string Name { get; set; } = Environment.MachineName;

    /// <summary>
    /// Which registered handler types and systems this node hosts; null (default) = everything compiled into the
    /// process, so the deployment unit is the project. Set it only to run several kinds of nodes in one process
    /// (in-process tests, embedding).
    /// </summary>
    public Func<Type, bool>? Hosts { get; set; }

    /// <summary>Address to bind the cluster (node-to-node) listener to.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Cluster port; 0 = pick a free port (tests).</summary>
    public int ClusterPort { get; set; }

    /// <summary>Host other nodes use to reach this node (pod IP / DNS name). Defaults to the bind address or loopback.</summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>Seed endpoints "host:port" (DNS names resolve to every address, e.g. a Kubernetes headless service).</summary>
    public List<string> Seeds { get; set; } = new();

    /// <summary>Number of shards (single-threaded shards); default one per core.</summary>
    public int Shards { get; set; } = Environment.ProcessorCount;

    /// <summary>ECS tick rate of every shard (Hz).</summary>
    public int TickRate { get; set; } = 20;

    /// <summary>
    /// Calls and events waiting in a shard's dispatch queue before new ones are refused with <see cref="Protocol.RpcStatus.Overloaded"/>
    /// (one-way calls and messages are dropped). Rounded up to a power of two. Replies, continuations and turn ends
    /// have a queue of their own and are never refused.
    /// </summary>
    public int DispatchQueueCapacity { get; set; } = 64 * 1024;

    /// <summary>Messages waiting for one actor before new ones are refused the same way.</summary>
    public int ActorMailboxCapacity { get; set; } = 4096;

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The shards' clock: ticks, delayed sends (<c>SendAfter</c>), idle unloading, checkpoints, and <c>Time</c> in actors,
    /// services and systems. Transport keeps real time (call deadlines, keep-alives, membership). Tests pass a manual
    /// clock (<c>DaServer.Testing</c>) and move it on step by step.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Where actors' <see cref="PersistAttribute">[Persist]</see> components are stored (ADR-068). Null (default):
    /// nothing is persisted, actors start from <see cref="Actor.OnLoad"/> every time.
    /// </summary>
    public Persistence.IActorStore? ActorStore { get; set; }

    /// <summary>
    /// The whole graceful stop (leave, drain, unload every actor, close) must fit in this budget: past it the node
    /// reports the actors that were not unloaded and exits instead of hanging. The host's shutdown timeout and the
    /// platform's grace period are set above it (DaServer.Hosting, DaServer.Aspire), so the platform never kills a node
    /// that is still saving.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// A shard pass running longer than this is a stall: something on the shard thread waits synchronously or loops
    /// (DAS009 reports the waits it can see), and every actor of the shard waits with it. It is logged and counted
    /// (<see cref="Node.StalledShards"/>).
    /// </summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Requests from outside the cluster (<see cref="DaServer.Ingress.IIngress"/>) are refused with
    /// <see cref="Protocol.RpcStatus.Overloaded"/> before they are sent when the target node gossiped at least this many
    /// queued messages: an overloaded node is not sent more work it would refuse anyway. 0 (default): no check.
    /// </summary>
    public int IngressLoadLimit { get; set; }

    /// <summary>
    /// Secret shared by every node of the cluster for signing (configuration <c>DaServer__SigningSecret</c>; Aspire
    /// generates one). Nodes prove it when they open a link (ADR-065): a process that only reaches the cluster port
    /// cannot join without it. Client-facing nodes sign
    /// resume tickets with it, so a client that lost its connection resumes its session on
    /// any of them (<see cref="SessionResumed"/>); features sign their tokens with it. Null: no tickets, a lost
    /// connection closes the session at once.
    /// </summary>
    public string? SigningSecret { get; set; }

    /// <summary>
    /// The cluster's name: nodes of another cluster that reach this one's port are refused (two environments sharing
    /// a network, a test cluster pointed at production seeds). Aspire sets the cluster resource's name.
    /// </summary>
    public string ClusterName { get; set; } = "daserver";

    /// <summary>
    /// How long actors wait for a client that lost its connection before they receive <see cref="SessionClosed"/>
    /// (when <see cref="SigningSecret"/> is set). A client that resumes in time is never reported closed; an explicit
    /// close (client dispose, <c>Disconnect</c>) is reported at once.
    /// </summary>
    public TimeSpan ResumeGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><see cref="ResumeGrace"/> where clients can resume at all (tickets need a <see cref="SigningSecret"/>), else zero.</summary>
    internal TimeSpan EffectiveResumeGrace => SigningSecret is { Length: > 0 } ? ResumeGrace : TimeSpan.Zero;

    /// <summary>
    /// Split brain (ADR-064): a node that finds itself on the minority side when members die (it reaches fewer of the
    /// cluster's members than it lost; in a tie, the side without the lowest node id) stops at once, without saving,
    /// and exits so the platform restarts it into the majority. Clusters of fewer than three members never down
    /// themselves (a crash and a partition look the same there). Off: both sides keep running and may load the same
    /// actor twice.
    /// </summary>
    public bool KeepMajority { get; set; } = true;

    public TimeSpan GossipInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SuspectAfter { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan DeadAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Unacknowledged bytes each node link keeps so its stream resumes after a disconnect (ADR-008). Acknowledgements
    /// arrive every 64 KB, so a link holds about what is in flight; a disconnect that needs more restarts the stream.
    /// </summary>
    public int LinkResendWindow { get; set; } = 32 * 1024 * 1024;

    /// <summary>log2 of the max number of concurrent outgoing calls per node.</summary>
    public int PendingCallBits { get; set; } = 18;

    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Capabilities this node runs (<see cref="NodeFeature"/>): none unless configured (the host adds the ones its
    /// configuration enables). Not bound from configuration itself.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<NodeFeature> Features { get; } = new();

    /// <summary>Tests: messages this node does not announce, as a node of an older build would not (V3).</summary>
    internal Func<uint, bool>? Unannounced { get; set; }

    internal IPEndPoint ClusterBindEndPoint => new(IPAddress.Parse(ListenAddress), ClusterPort);
}
