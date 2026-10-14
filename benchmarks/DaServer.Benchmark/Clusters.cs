using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Runtime;

namespace DaServer.Benchmark;

/// <summary>Real nodes on loopback TCP, production defaults except membership timings (fast convergence).</summary>
internal static class Clusters
{
    public static async Task<Node[]> Start(int nodes, int shards, bool gateway = false)
    {
        var started = new Node[nodes];
        for (int i = 0; i < nodes; i++)
        {
            started[i] = new Node(new NodeOptions
            {
                Name = $"bench-{i}",
                ClusterBind = "127.0.0.1",
                ClientPort = gateway && i == 0 ? 0 : null,
                Shards = shards,
                Seeds = i == 0 ? [] : [$"127.0.0.1:{started[0].ClusterEndPoint.Port}"],
                GossipInterval = TimeSpan.FromMilliseconds(100),
            });
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
