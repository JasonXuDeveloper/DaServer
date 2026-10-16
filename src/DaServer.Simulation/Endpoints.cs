using System.Net;

namespace DaServer.Simulation;

/// <summary>The client entry addresses Aspire's <c>WithClientEndpoints</c> sets (<c>DaServer__ClientEndpoints__{i}</c>).</summary>
public static class Endpoints
{
    public static IReadOnlyList<DnsEndPoint> FromEnvironment(string fallback = "127.0.0.1:7100")
    {
        var endpoints = new List<DnsEndPoint>();
        for (int i = 0; Environment.GetEnvironmentVariable($"DaServer__ClientEndpoints__{i}") is { } address; i++) endpoints.Add(Parse(address));
        if (endpoints.Count == 0) endpoints.Add(Parse(fallback));
        return endpoints;
    }

    private static DnsEndPoint Parse(string address)
    {
        int colon = address.LastIndexOf(':');
        return new DnsEndPoint(address[..colon], int.Parse(address[(colon + 1)..]));
    }
}
