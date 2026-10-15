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

    /// <summary>Only direct clients to the other client-facing nodes, keep no sessions (a well-known address).</summary>
    public bool Balancer { get; set; }
}
