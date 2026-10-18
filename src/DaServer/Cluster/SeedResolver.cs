using System.Net;
using System.Net.Sockets;

namespace DaServer.Cluster;

/// <summary>
/// Turns a configured seed (<see cref="NodeOptions.Seeds"/>) into the endpoints of the nodes it names. A node that is
/// alone asks it every few seconds, off the gossip round: resolving may wait on DNS or a registry, gossip does not.
/// </summary>
public interface ISeedResolver
{
    /// <summary>
    /// The endpoints <paramref name="seed"/> names, empty when it names none yet (a DNS name not published yet). Throws
    /// only for a seed that can never resolve.
    /// </summary>
    ValueTask<IReadOnlyList<DnsEndPoint>> ResolveAsync(string seed, CancellationToken cancellationToken);
}

/// <summary>
/// The default: "host:port", a DNS name resolving to every address it has (Compose's service name for every replica, a
/// Kubernetes headless Service for every pod). <c>DaServer.Hosting</c> resolves seeds written as service names
/// (<c>tcp://name</c>) through .NET service discovery and the rest through this.
/// </summary>
public sealed class DnsSeedResolver : ISeedResolver
{
    public static readonly DnsSeedResolver Instance = new();

    public async ValueTask<IReadOnlyList<DnsEndPoint>> ResolveAsync(string seed, CancellationToken cancellationToken)
    {
        if (!HostPort.TryParse(seed, out var host, out int port)) throw new FormatException($"Seed '{seed}' is not host:port");
        if (IPAddress.TryParse(host, out _) || host == "localhost") return [new DnsEndPoint(host, port)];
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var endpoints = new List<DnsEndPoint>(addresses.Length);
            foreach (var address in addresses)
                endpoints.Add(new DnsEndPoint((address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString(), port));
            return endpoints;
        }
        catch (SocketException)
        {
            return []; // not published yet (a peer's DNS record appears once it runs): asked again later
        }
    }
}
