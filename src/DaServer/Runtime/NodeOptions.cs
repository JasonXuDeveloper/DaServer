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

    /// <summary>Client (gateway) port; null = this node accepts no game clients.</summary>
    public int? ClientPort { get; set; }

    /// <summary>Host game clients use to reach this gateway (public DNS name or address); defaults to AdvertisedHost.</summary>
    public string? ClientAdvertisedHost { get; set; }

    /// <summary>
    /// A balancer (the clients' entry gateway): clients connecting to it are directed to the least loaded other gateway
    /// (<c>ClusterClient.ConnectAsync</c> follows automatically), so clients need one well-known address only.
    /// </summary>
    public bool Balancer { get; set; }

    /// <summary>Seed endpoints "host:port" (DNS names resolve to every address, e.g. a Kubernetes headless service).</summary>
    public List<string> Seeds { get; set; } = new();

    /// <summary>Largest frame a game client may send (gateway); larger frames close the session.</summary>
    public int MaxClientFrameSize { get; set; } = 64 * 1024;

    /// <summary>Number of shards (single-threaded shards); default one per core.</summary>
    public int Shards { get; set; } = Environment.ProcessorCount;

    /// <summary>ECS tick rate of every shard (Hz).</summary>
    public int TickRate { get; set; } = 20;

    /// <summary>
    /// Calls and events waiting in a shard's dispatch queue before new ones are refused with <see cref="Protocol.RpcStatus.Overloaded"/>
    /// (one-way calls and messages are dropped).
    /// </summary>
    public int DispatchQueueCapacity { get; set; } = 64 * 1024;

    /// <summary>Messages waiting for one actor before new ones are refused the same way.</summary>
    public int ActorMailboxCapacity { get; set; } = 4096;

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Secret shared by every node of the cluster (configuration <c>DaServer__ClusterSecret</c>; Aspire generates one).
    /// Gateways sign reconnect tickets with it, so a client that lost its connection resumes its session on any
    /// gateway (<see cref="Rpc.SessionResync"/>). Null: no tickets, a lost connection closes the session at once.
    /// </summary>
    public string? ClusterSecret { get; set; }

    /// <summary>
    /// How long actors wait for a client that lost its connection before they receive <see cref="Rpc.SessionClosed"/>
    /// (when <see cref="ClusterSecret"/> is set). A client that resumes in time is never reported closed; an explicit
    /// close (client dispose, <c>Disconnect</c>) is reported at once.
    /// </summary>
    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validity of a reconnect ticket; clients refresh theirs at half of it.</summary>
    public TimeSpan ReconnectTicketLifetime { get; set; } = TimeSpan.FromMinutes(10);
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

    internal IPEndPoint ClusterBindEndPoint => new(IPAddress.Parse(ListenAddress), ClusterPort);
}
