using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DaServer.Tests;

/// <summary>Nodes run the way the Generic Host runs them (AddDaServer, MapDaServer), on loopback and any free ports.</summary>
internal static class HostedNodes
{
    /// <summary>
    /// Start a hosted node. Without <paramref name="cluster"/> it gets a cluster of its own, named as
    /// <see cref="TestNodes.NodeOptionsFor"/> names one (a port an earlier test's node just released must not join this
    /// node to that test's cluster). <paramref name="settings"/> add to or override the configuration,
    /// <paramref name="configure"/> adjusts the builder after AddDaServer, <paramref name="map"/> maps endpoints before
    /// MapDaServer.
    /// </summary>
    public static async Task<WebApplication> StartAsync(string name, int shards = 2, string? cluster = null,
        Dictionary<string, string?>? settings = null, Action<WebApplicationBuilder>? configure = null, Action<WebApplication>? map = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["DaServer:Name"] = name,
            ["DaServer:ClusterName"] = cluster ?? TestNodes.NodeOptionsFor(name, shards, tickRate: 0, seed: null).ClusterName,
            ["DaServer:ListenAddress"] = "127.0.0.1",
            ["DaServer:ClusterPort"] = "0",
            ["DaServer:HttpPort"] = "0",
            ["DaServer:Shards"] = shards.ToString(),
            ["DaServer:StatsInterval"] = "0",
        };
        foreach (var (key, value) in settings ?? []) configuration[key] = value;

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddDaServer();
        configure?.Invoke(builder);
        var app = builder.Build();
        map?.Invoke(app);
        app.MapDaServer();
        await app.StartAsync();
        return app;
    }

    /// <summary>The node <paramref name="app"/> runs.</summary>
    public static Node Node(this WebApplication app) => app.Services.GetRequiredService<Node>();

    /// <summary>The node <paramref name="app"/> runs, once it joined its cluster.</summary>
    public static async Task<Node> ReadyNode(this WebApplication app)
    {
        var node = app.Node();
        await TestNodes.WaitUntilAsync(() => node.IsReady, TimeSpan.FromSeconds(10), "the node did not join");
        return node;
    }
}
