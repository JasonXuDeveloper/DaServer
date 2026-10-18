using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DaServer.Tests;

/// <summary>Seeds named as services resolve through .NET service discovery in a hosted node.</summary>
public class SeedDiscoveryTests
{
    [Test]
    public async Task ASeedNamedAsAServiceResolvesThroughServiceDiscovery()
    {
        string cluster = "seeds-" + Guid.NewGuid().ToString("N");
        await using var a = await StartNode("a", cluster, []);
        int port = a.Services.GetRequiredService<Node>().ClusterEndPoint.Port;
        // What an Aspire reference to a's cluster endpoint writes (services__peer__tcp__0), read by b's service discovery.
        await using var b = await StartNode("b", cluster, new() { ["DaServer:Seeds:0"] = "tcp://peer", ["services:peer:tcp:0"] = $"tcp://127.0.0.1:{port}" });
        var nodes = new[] { a.Services.GetRequiredService<Node>(), b.Services.GetRequiredService<Node>() };
        await TestNodes.WaitUntilAsync(() => TestNodes.Converged(nodes), TimeSpan.FromSeconds(15), "b found a through service discovery");
    }

    private static async Task<WebApplication> StartNode(string name, string cluster, Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(settings)
        {
            ["DaServer:Name"] = name,
            ["DaServer:ClusterName"] = cluster,
            ["DaServer:ListenAddress"] = "127.0.0.1",
            ["DaServer:ClusterPort"] = "0",
            ["DaServer:HttpPort"] = "0",
            ["DaServer:Shards"] = "1",
            ["DaServer:StatsInterval"] = "0",
            ["DaServer:GossipInterval"] = "00:00:00.1",
        });
        builder.AddDaServer();
        var app = builder.Build();
        app.MapDaServer();
        await app.StartAsync();
        return app;
    }
}
