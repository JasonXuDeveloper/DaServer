namespace DaServer.Benchmark;

/// <summary>Real nodes on loopback TCP, production defaults except membership timings (fast convergence).</summary>
internal static class Clusters
{
    /// <summary>Start <paramref name="nodes"/> nodes; the first <paramref name="gateways"/> accept clients.</summary>
    public static async Task<Node[]> Start(int nodes, int shards, int gateways = 0, Action<NodeOptions>? configure = null)
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
            if (i < gateways) options.Features.Add(new ClientConnections(new ClientConnectionOptions()));
            configure?.Invoke(options);
            started[i] = new Node(options);
            await started[i].StartAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Array.Exists(started, n => n.View.UpMembers.Length != nodes))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("cluster did not converge");
            await Task.Delay(50);
        }

        return started;
    }

    /// <summary>First id from <paramref name="start"/> whose actor lives on <paramref name="owner"/> in <paramref name="node"/>'s view.</summary>
    public static TId IdOwnedBy<TId>(Node node, Node owner, long start = 1) where TId : struct, IActorId
    {
        long key = start;
        while (node.View.HandlerTypeOwner(ActorIds<TId>.HandlerTypeId, key)!.NodeId != owner.Id) key++;
        return ActorIds<TId>.Of(key);
    }

    public static int ClientPort(this Node node) => node.Feature<ClientConnections>()!.Port;

    public static async Task Stop(IEnumerable<Node> nodes)
    {
        foreach (var node in nodes) await node.StopAsync();
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
