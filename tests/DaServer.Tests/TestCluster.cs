using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DaServer.Client;
using DaServer.Runtime;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>
/// Base class of integration tests: an in-process cluster on loopback with fast membership timings, torn down after
/// every test (nodes and clients).
/// </summary>
public abstract class ClusterTest
{
    private readonly List<Node> _nodes = [];
    private readonly List<GameClient> _clients = [];

    protected IReadOnlyList<Node> Nodes => _nodes;

    [TearDown]
    public async Task TearDownCluster()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        foreach (var node in _nodes) await node.StopAsync();
        _clients.Clear();
        _nodes.Clear();
    }

    /// <summary>Start a node; every node after the first joins through the first one.</summary>
    protected async Task<Node> Start(string name, int shards = 2, bool gateway = false, string[]? roles = null,
        Action<NodeOptions>? configure = null)
    {
        var options = new NodeOptions
        {
            Name = name,
            ClusterBind = "127.0.0.1",
            ClusterPort = 0,
            ClientPort = gateway ? 0 : null,
            Shards = shards,
            TickRate = 50,
            Seeds = _nodes.Count == 0 ? [] : [$"127.0.0.1:{_nodes[0].ClusterEndPoint.Port}"],
            Roles = roles?.ToList() ?? [],
            GossipInterval = TimeSpan.FromMilliseconds(100),
            SuspectAfter = TimeSpan.FromMilliseconds(600),
            DeadAfter = TimeSpan.FromMilliseconds(1500),
            CallTimeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(options);

        var node = new Node(options);
        await node.StartAsync();
        _nodes.Add(node);
        return node;
    }

    /// <summary>Start <paramref name="count"/> nodes and wait until they all see each other.</summary>
    protected async Task<Node[]> StartCluster(int count, int shards = 2)
    {
        var nodes = new Node[count];
        for (int i = 0; i < count; i++) nodes[i] = await Start(((char)('a' + i)).ToString(), shards);
        await Converged();
        return nodes;
    }

    /// <summary>Wait until every running node sees every other one as Up.</summary>
    protected Task Converged() =>
        Eventually(() => _nodes.All(n => n.View.UpMembers.Length == _nodes.Count), "cluster did not converge");

    protected async Task Stop(Node node)
    {
        await node.StopAsync();
        _nodes.Remove(node);
    }

    protected async Task<GameClient> Connect(Node gateway)
    {
        var client = new GameClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, gateway.ClientPort));
        _clients.Add(client);
        return client;
    }

    /// <summary>First key from <paramref name="start"/> whose actor of <paramref name="contractId"/> lives on <paramref name="owner"/>.</summary>
    protected static long KeyOwnedBy(Node owner, uint contractId, long start = 1)
    {
        long key = start;
        while (owner.View.ActorOwner(contractId, key)!.NodeId != owner.Id) key++;
        return key;
    }

    /// <summary>Polls a condition that depends on asynchronous delivery (membership, pushes to other sockets).</summary>
    protected static async Task Eventually(Func<bool> condition, string message, int timeoutMs = 10_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) Assert.Fail(message);
            await Task.Delay(10);
        }
    }
}
