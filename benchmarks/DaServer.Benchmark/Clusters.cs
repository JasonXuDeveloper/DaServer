using DaServer.Client;

namespace DaServer.Benchmark;

/// <summary>Real nodes on loopback TCP, production defaults except membership timings (fast convergence).</summary>
internal static class Clusters
{
    /// <summary>Start <paramref name="nodes"/> nodes; the first <paramref name="gateways"/> accept clients.</summary>
    public static async Task<Node[]> Start(int nodes, int shards, int gateways = 0, Action<NodeOptions>? configure = null,
        TimeSpan clientFlush = default)
    {
        var started = new Node[nodes];
        for (int i = 0; i < nodes; i++)
        {
            var options = new NodeOptions
            {
                Name = $"bench-{i}",
                ListenAddress = "127.0.0.1",
                Shards = shards,
                Seeds = i == 0 ? [] : [$"127.0.0.1:{started[0].ClusterEndPoint.Port}"],
                GossipInterval = TimeSpan.FromMilliseconds(100),
            };
            if (i < gateways) options.Features.Add(new ClientConnections(new ClientConnectionOptions { FlushInterval = clientFlush }));
            configure?.Invoke(options);
            started[i] = new Node(options);
            await started[i].StartAsync();
        }

        await TestNodes.WaitUntilAsync(() => TestNodes.Converged(started), TimeSpan.FromSeconds(30), "cluster did not converge");
        return started;
    }

    /// <summary>First id from <paramref name="start"/> whose actor lives on <paramref name="owner"/> in <paramref name="node"/>'s view.</summary>
    public static TId IdOwnedBy<TId>(Node node, Node owner, long start = 1) where TId : struct, IActorId =>
        ActorIds<TId>.Of(node.KeyOwnedBy<TId>(owner, start));

    /// <summary>Connect <paramref name="count"/> clients, client i to the gateway <paramref name="gateways"/>[i % length], each handled by <paramref name="handler"/>.</summary>
    public static async Task<ClusterClient[]> Connect(int count, Node[] gateways, object handler)
    {
        var clients = new ClusterClient[count];
        for (int i = 0; i < count; i++)
        {
            var client = clients[i] = new ClusterClient();
            await client.ConnectAsync("127.0.0.1", gateways[i % gateways.Length].ClientPort(), followRedirect: false);
            client.Register(handler);
        }

        return clients;
    }

    /// <summary>Dispose the clients, then stop the nodes.</summary>
    public static void Stop(Node[] nodes, ClusterClient[]? clients = null)
    {
        foreach (var client in clients ?? []) client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var node in nodes) node.StopAsync().GetAwaiter().GetResult();
    }
}

/// <summary>Counts deliveries and releases a waiter when the expected number arrived (allocation-free per delivery).</summary>
internal sealed class Countdown
{
    private int _remaining;
    private TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reset(int count)
    {
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _remaining, count);
        return _done.Task;
    }

    public void Signal()
    {
        if (Interlocked.Decrement(ref _remaining) == 0) _done.TrySetResult();
    }
}
