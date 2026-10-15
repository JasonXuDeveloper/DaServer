namespace DaServer.Clients;

/// <summary>Client connections of a node (<c>DaServer:Clients</c> in configuration; the section enables them).</summary>
public sealed class ClientOptions
{
    /// <summary>Port clients connect to (0: any free port, for tests).</summary>
    public int Port { get; set; }

    /// <summary>Host clients use to reach this node (public DNS name or address); defaults to the node's advertised host.</summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>Largest frame a client may send (what an untrusted client may make this node buffer).</summary>
    public int MaxFrameSize { get; set; } = 64 * 1024;

    /// <summary>Validity of a reconnect ticket; clients refresh theirs at half of it.</summary>
    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Clients that connect are directed to the less loaded of two client-facing nodes (their gossiped sessions) and
    /// move there: clients know one well-known address (the platform's load balancer or DNS name), the cluster spreads
    /// them. False when the nodes sit behind a load balancer without addresses of their own: clients stay.
    /// </summary>
    public bool Redirect { get; set; } = true;
}
