using System.Diagnostics;
using DaServer.Clients;
using DaServer.Runtime;

namespace DaServer.Testing;

/// <summary>Node helpers shared by <see cref="TestCluster"/>, the framework's own tests and its benchmarks.</summary>
internal static class TestNodes
{
    /// <summary>A test node's options: loopback, any free port, fast membership timings, joining through <paramref name="seed"/>.</summary>
    public static NodeOptions NodeOptionsFor(string name, int shards, int tickRate, Node? seed) => new()
    {
        Name = name,
        ListenAddress = "127.0.0.1",
        Shards = shards,
        TickRate = tickRate,
        Seeds = seed == null ? new() : new() { "127.0.0.1:" + seed.ClusterEndPoint.Port },
        GossipInterval = TimeSpan.FromMilliseconds(100),
        SuspectAfter = TimeSpan.FromMilliseconds(600),
        DeadAfter = TimeSpan.FromMilliseconds(1500),
        CallTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>Start a node with <paramref name="options"/>, accepting clients when <paramref name="clients"/> is given.</summary>
    public static async Task<Node> StartAsync(NodeOptions options, ClientConnectionOptions? clients = null)
    {
        if (clients != null) options.Features.Add(new ClientConnections(clients));
        var node = new Node(options);
        await node.StartAsync().ConfigureAwait(false);
        return node;
    }

    /// <summary>Whether every node sees every one of <paramref name="nodes"/> as Up.</summary>
    public static bool Converged(IReadOnlyList<Node> nodes)
    {
        foreach (var node in nodes)
            if (node.View.UpMembers.Length != nodes.Count) return false;
        return true;
    }

    /// <summary>Node owning the actor of <paramref name="key"/> in <paramref name="node"/>'s view.</summary>
    public static ulong OwnerOf<TId>(this Node node, long key) where TId : struct, IActorId =>
        node.View.HandlerTypeOwner(ActorIds<TId>.HandlerTypeId, key)!.NodeId;

    /// <summary>First key from <paramref name="start"/> whose actor lives on <paramref name="owner"/> in <paramref name="node"/>'s view.</summary>
    public static long KeyOwnedBy<TId>(this Node node, Node owner, long start = 1) where TId : struct, IActorId
    {
        long key = start;
        while (node.OwnerOf<TId>(key) != owner.Id) key++;
        return key;
    }

    public static int ClientPort(this Node node) => node.Feature<ClientConnections>()!.Port;

    public static int SessionCount(this Node node) => node.Feature<ClientConnections>()?.SessionCount ?? 0;

    /// <summary>Poll <paramref name="condition"/> (asynchronous delivery: pushes, other nodes) until it holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string? message = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException(message ?? "The condition did not hold in time");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    /// <summary>Poll an asynchronous condition (a call into the cluster) until it holds.</summary>
    public static async Task WaitUntilAsync(Func<ValueTask<bool>> condition, TimeSpan timeout, string? message = null)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition().ConfigureAwait(false))
        {
            if (watch.Elapsed > timeout) throw new TimeoutException(message ?? "The condition did not hold in time");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
