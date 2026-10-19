using Microsoft.AspNetCore.Builder;

namespace DaServer.Tests;

/// <summary>Seeds named as services resolve through .NET service discovery in a hosted node.</summary>
public class SeedDiscoveryTests
{
    [Test]
    public async Task ASeedNamedAsAServiceResolvesThroughServiceDiscovery()
    {
        await using var a = await StartNode("a", cluster: null, []);
        string cluster = a.Node().Options.ClusterName;
        int port = a.Node().ClusterEndPoint.Port;
        // What an Aspire reference to a's cluster endpoint writes (services__peer__tcp__0), read by b's service discovery.
        await using var b = await StartNode("b", cluster, new() { ["DaServer:Seeds:0"] = "tcp://peer", ["services:peer:tcp:0"] = $"tcp://127.0.0.1:{port}" });
        var nodes = new[] { a.Node(), b.Node() };
        await TestNodes.WaitUntilAsync(() => TestNodes.Converged(nodes), TimeSpan.FromSeconds(15), "b found a through service discovery");
    }

    private static Task<WebApplication> StartNode(string name, string? cluster, Dictionary<string, string?> settings)
    {
        settings["DaServer:GossipInterval"] = "00:00:00.1";
        return HostedNodes.StartAsync(name, shards: 1, cluster, settings);
    }
}
