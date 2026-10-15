using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Client;
using DaServer.Rpc;
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
    private readonly List<ClusterClient> _clients = [];

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
    protected async Task<Node> Start(string name, int shards = 2, bool gateway = false, Func<Type, bool>? hosts = null,
        Action<NodeOptions>? configure = null)
    {
        var options = new NodeOptions
        {
            Name = name,
            ListenAddress = "127.0.0.1",
            ClusterPort = 0,
            ClientPort = gateway ? 0 : null,
            Shards = shards,
            TickRate = 50,
            Seeds = _nodes.Count == 0 ? [] : [$"127.0.0.1:{_nodes[0].ClusterEndPoint.Port}"],
            Hosts = hosts,
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

    /// <summary>Dispose <paramref name="client"/> with the cluster.</summary>
    protected ClusterClient Track(ClusterClient client)
    {
        _clients.Add(client);
        return client;
    }

    protected async Task<ClusterClient> Connect(Node gateway)
    {
        var client = new ClusterClient();
        await client.ConnectAsync("127.0.0.1", gateway.ClientPort, followBalancer: false);
        _clients.Add(client);
        return client;
    }

    /// <summary>First key from <paramref name="start"/> whose actor of <paramref name="contractId"/> lives on <paramref name="owner"/>.</summary>
    protected static long KeyOwnedBy<TActor>(Node owner, long start = 1) where TActor : Actor
    {
        long key = start;
        while (OwnerOf<TActor>(owner, key) != owner.Id) key++;
        return key;
    }

    /// <summary>Node owning the <typeparamref name="TActor"/> of <paramref name="key"/> in <paramref name="node"/>'s view.</summary>
    protected static ulong OwnerOf<TActor>(Node node, long key) where TActor : Actor =>
        node.View.ImplementationOwner(ImplementationId<TActor>(), key)!.NodeId;

    /// <summary>Placement identity of an actor or service type (FNV-1a of its full name).</summary>
    protected static uint ImplementationId<T>() => RpcIds.Fnv1a(typeof(T).FullName!);

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

    /// <summary>Polls an asynchronous condition (a call into the cluster) until it holds.</summary>
    protected static async Task Eventually(Func<ValueTask<bool>> condition, string message, int timeoutMs = 10_000)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) Assert.Fail(message);
            await Task.Delay(10);
        }
    }
}
