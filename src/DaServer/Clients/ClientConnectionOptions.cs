namespace DaServer.Clients;

/// <summary>Client connections of a node (<c>DaServer:Clients</c> in configuration; the section enables them).</summary>
public sealed class ClientConnectionOptions
{
    /// <summary>Port clients connect to (0: any free port, for tests).</summary>
    public int Port { get; set; }

    /// <summary>Host clients use to reach this node (public DNS name or address); defaults to the node's advertised host.</summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>Largest frame a client may send (what an untrusted client may make this node buffer).</summary>
    public int MaxFrameSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Messages a client may send per second on average (0: no limit); beyond it requests are refused with
    /// <see cref="Protocol.RpcStatus.RateLimited"/> and when to retry, one-way messages are dropped.
    /// </summary>
    public double MessagesPerSecond { get; set; }

    /// <summary>Messages a client may send at once above <see cref="MessagesPerSecond"/>.</summary>
    public double Burst { get; set; } = 100;

    /// <summary>
    /// A client that sent nothing for this long is gone (half-open TCP, a dropped NAT mapping, a switched network, an
    /// app in the background): its connection is closed as lost, so its session waits out the resume grace (ADR-053).
    /// Idle clients ping (<c>ClusterClient.KeepAliveInterval</c>, 15 s): keep this a few intervals above it.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Validity of a resume ticket; clients refresh theirs at half of it.</summary>
    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Clients that connect are directed to the less loaded of two client-facing nodes (their gossiped sessions) and
    /// move there: clients know one well-known address (the platform's load balancer or DNS name), the cluster spreads
    /// them. False when the nodes sit behind a load balancer without addresses of their own: clients stay.
    /// </summary>
    public bool Redirect { get; set; } = true;

    /// <summary>
    /// When the node leaves, its clients' connections are closed in waves over this time, so they reconnect to the
    /// other nodes (resuming their sessions) gradually rather than all at once. Part of the node's stop budget.
    /// </summary>
    public TimeSpan DrainSpread { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Write to clients on this beat instead of at once (zero, the default: at once). What a session receives within one
    /// interval (a map's positions, a broadcast from another node) leaves in one write, from one flush thread: fewer
    /// writes for the node and fewer reads for the client, everything sent to clients (replies included) up to one
    /// interval later. For high-rate pushes on many sessions (a game's tick); keep it below the tick.
    /// </summary>
    public TimeSpan FlushInterval { get; set; }
}
