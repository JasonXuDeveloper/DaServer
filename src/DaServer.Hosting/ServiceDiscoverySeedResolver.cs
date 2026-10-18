using System.Net;
using DaServer.Cluster;
using Microsoft.Extensions.ServiceDiscovery;

namespace DaServer.Hosting;

/// <summary>
/// Seeds through .NET service discovery (what <c>AddDaServer</c> sets): a seed written as a service name
/// (<c>tcp://peers</c>, <c>_cluster.peers</c>) is resolved by the host's endpoint providers: configuration
/// (<c>services:peers:tcp:0</c>, what Aspire references write), DNS SRV records once the application adds that provider
/// (<c>AddDnsSrvServiceEndpointProvider</c>: a Kubernetes headless Service's pods), or any registry with a provider.
/// "host:port" stays DNS (<see cref="DnsSeedResolver"/>), and so does every endpoint a provider returns as a name.
/// </summary>
internal sealed class ServiceDiscoverySeedResolver(ServiceEndpointResolver resolver) : ISeedResolver
{
    public async ValueTask<IReadOnlyList<DnsEndPoint>> ResolveAsync(string seed, CancellationToken cancellationToken)
    {
        if (!seed.Contains("://", StringComparison.Ordinal) && !seed.StartsWith('_'))
            return await DnsSeedResolver.Instance.ResolveAsync(seed, cancellationToken);

        var source = await resolver.GetEndpointsAsync(seed, cancellationToken);
        var endpoints = new List<DnsEndPoint>();
        foreach (var endpoint in source.Endpoints)
            switch (endpoint.EndPoint)
            {
                case IPEndPoint ip:
                    endpoints.Add(new DnsEndPoint((ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address).ToString(), ip.Port));
                    break;
                case DnsEndPoint name:
                    endpoints.AddRange(await DnsSeedResolver.Instance.ResolveAsync($"{name.Host}:{name.Port}", cancellationToken));
                    break;
                case UriEndPoint { Uri: { Port: > 0 } uri }: // configuration's (and Aspire's) tcp://host:port
                    endpoints.AddRange(await DnsSeedResolver.Instance.ResolveAsync($"{uri.Host}:{uri.Port}", cancellationToken));
                    break;
            }

        return endpoints;
    }
}
