using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DaServer.Client;
using DaServer.Runtime;

namespace DaServer.Tests;

/// <summary>In-process cluster on loopback with fast membership timings, for integration tests.</summary>
public sealed class TestCluster : IAsyncDisposable
{
    private readonly List<Node> _nodes = new();
    private readonly List<GameClient> _clients = new();

    public IReadOnlyList<Node> Nodes => _nodes;

    public static NodeOptions Options(string name, int shards = 2, string? seed = null, int? clientPort = null,
        params string[] roles) => new()
    {
        Name = name,
        ClusterBind = "127.0.0.1",
        ClusterPort = 0,
        ClientPort = clientPort,
        Shards = shards,
        TickRate = 50,
        Seeds = seed == null ? new() : new() { seed },
        Roles = roles.ToList(),
        GossipInterval = TimeSpan.FromMilliseconds(100),
        SuspectAfter = TimeSpan.FromMilliseconds(600),
        DeadAfter = TimeSpan.FromMilliseconds(1500),
        CallTimeout = TimeSpan.FromSeconds(5),
    };

    public async Task<Node> StartNode(NodeOptions options)
    {
        var node = new Node(options);
        await node.StartAsync();
        _nodes.Add(node);
        return node;
    }

    /// <summary>Start a node that joins through the first node of the cluster.</summary>
    public Task<Node> Join(string name, int shards = 2, int? clientPort = null, params string[] roles) =>
        StartNode(Options(name, shards, Seed, clientPort, roles));

    public string Seed => $"127.0.0.1:{_nodes[0].ClusterEndPoint.Port}";

    /// <summary>Wait until every running node sees <paramref name="count"/> Up members.</summary>
    public Task Converged(int count) => Eventually(() => _nodes.All(n => n.View.UpMembers.Length == count),
        $"cluster did not converge to {count} members");

    public async Task<GameClient> Connect(Node gateway)
    {
        var client = new GameClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, gateway.ClientPort));
        _clients.Add(client);
        return client;
    }

    public async Task Stop(Node node)
    {
        await node.StopAsync();
        _nodes.Remove(node);
    }

    public static async Task Eventually(Func<bool> condition, string message, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        foreach (var node in _nodes) await node.StopAsync();
    }
}
