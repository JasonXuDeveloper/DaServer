using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
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
            configure?.Invoke(options);
            started[i] = await TestNodes.StartAsync(options, i < gateways ? new ClientConnectionOptions { FlushInterval = clientFlush } : null);
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
            await client.ConnectAsync("127.0.0.1", gateways[i % gateways.Length].ClientPort());
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

/// <summary>
/// Counts deliveries and releases a waiter when the expected number arrived. Reused for every operation (one waiter at a
/// time), so the measurement shows what the path under test allocates, not the harness.
/// </summary>
internal sealed class Countdown : IValueTaskSource
{
    private int _remaining;
    private ManualResetValueTaskSourceCore<bool> _done = new() { RunContinuationsAsynchronously = true };

    public ValueTask Reset(int count)
    {
        _done.Reset();
        Volatile.Write(ref _remaining, count);
        return new ValueTask(this, _done.Version);
    }

    public void Signal()
    {
        if (Interlocked.Decrement(ref _remaining) == 0) _done.SetResult(true);
    }

    void IValueTaskSource.GetResult(short token) => _done.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _done.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _done.OnCompleted(continuation, state, token, flags);
}
