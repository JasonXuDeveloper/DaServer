using System.Diagnostics;
using DaServer.Clients;
using DaServer.Client;
using DaServer.Runtime;

namespace DaServer.Testing;

/// <summary>
/// An in-process cluster for tests of actors, services and systems: real nodes on loopback (the same runtime, wire
/// and placement as production) with fast membership timings. With <see cref="TestClusterOptions.ManualTime"/> the
/// shards run on a clock the test moves: <c>await cluster.TickAsync(10)</c> runs exactly ten ticks on every shard and
/// fires what fell due (delayed sends, idle unloads, checkpoints). Messages still flow as they arrive.
/// <code>
/// await using var cluster = await TestCluster.StartAsync(o => o.ManualTime = true);
/// cluster.Cluster.Send(new MapId(1), new Enter(player));
/// await cluster.TickAsync(20);
/// var info = await cluster.Cluster.Ask(new MapId(1), new GetInfo());
/// </code>
/// </summary>
public sealed class TestCluster : IAsyncDisposable
{
    private readonly TestClusterOptions _options;
    private readonly List<Node> _nodes = new();
    private readonly List<ClusterClient> _clients = new();
    private readonly ManualClock? _clock;
    private static readonly TimeSpan TickTimeout = TimeSpan.FromSeconds(10);

    private TestCluster(TestClusterOptions options)
    {
        _options = options;
        if (options.ManualTime) _clock = new ManualClock();
    }

    /// <summary>Start the nodes and wait until they all see each other.</summary>
    public static async Task<TestCluster> StartAsync(Action<TestClusterOptions>? configure = null)
    {
        var options = new TestClusterOptions();
        configure?.Invoke(options);
        var cluster = new TestCluster(options);
        try
        {
            for (int i = 0; i < options.Nodes; i++) await cluster.AddNodeAsync(i);
            await cluster.WaitUntilAsync(() => TestNodes.Converged(cluster._nodes), TimeSpan.FromSeconds(10), "the nodes did not see each other");
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    /// <summary>The cluster as the first node sees it: what business code sees (<c>Send</c>, <c>Ask</c>, <c>Publish</c>).</summary>
    public ICluster Cluster => _nodes[0];

    /// <summary>The cluster as node <paramref name="index"/> sees it.</summary>
    public ICluster Node(int index) => _nodes[index];

    /// <summary>The shards' clock: manual with <see cref="TestClusterOptions.ManualTime"/>, the system's otherwise.</summary>
    public TimeProvider Time => _clock ?? TimeProvider.System;

    /// <summary>
    /// Move the manual clock by <paramref name="ticks"/> ticks, one at a time: each runs once on every shard of every
    /// node (systems, then what fell due) before the next starts.
    /// </summary>
    public async Task TickAsync(int ticks = 1)
    {
        if (_clock == null) throw new InvalidOperationException("Ticks are stepped with TestClusterOptions.ManualTime only");
        long interval = _clock.TimestampFrequency / Math.Max(1, _options.TickRate); // as the shards count it
        for (int t = 0; t < ticks; t++)
        {
            long target = 0;
            foreach (var node in _nodes)
            foreach (var shard in node.Shards)
                target = Math.Max(target, shard.TickCount);
            _clock.Advance(interval);
            var watch = Stopwatch.StartNew();
            foreach (var node in _nodes)
            foreach (var shard in node.Shards)
            {
                shard.Wake();
                while (shard.TickCount <= target && !shard.Stopped.IsCompleted)
                {
                    if (watch.Elapsed > TickTimeout) throw new TimeoutException($"Shard {shard.Index} of {node.Options.Name} did not tick: blocked?");
                    await Task.Yield();
                }
            }
        }
    }

    /// <summary>Move the manual clock by <paramref name="time"/>, in whole ticks (<see cref="TickAsync"/>).</summary>
    public Task AdvanceAsync(TimeSpan time) => TickAsync((int)Math.Ceiling(time.TotalSeconds * _options.TickRate));

    /// <summary>A client connected to the first node (<see cref="TestClusterOptions.ClientConnections"/>), disposed with the cluster.</summary>
    public async Task<ClusterClient> ConnectAsync(object? handler = null)
    {
        var connections = _nodes[0].Feature<ClientConnections>()
            ?? throw new InvalidOperationException("Clients connect with TestClusterOptions.ClientConnections only");
        var client = new ClusterClient();
        _clients.Add(client);
        if (handler != null) client.Register(handler);
        await client.ConnectAsync("127.0.0.1", connections.Port).ConfigureAwait(false);
        return client;
    }

    /// <summary>Poll <paramref name="condition"/> (asynchronous delivery: pushes, other nodes) until it holds.</summary>
    public Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string? message = null) =>
        TestNodes.WaitUntilAsync(condition, timeout, message);

    /// <summary>Stop the nodes (actors unload, records are written) and dispose the clients.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync().ConfigureAwait(false);
        for (int i = _nodes.Count - 1; i >= 0; i--) await _nodes[i].StopAsync().ConfigureAwait(false);
        _clients.Clear();
        _nodes.Clear();
    }

    internal IReadOnlyList<Node> Nodes => _nodes;

    /// <summary>Start node <paramref name="index"/>; every node after the first joins through the first one.</summary>
    internal async Task<Node> AddNodeAsync(int index)
    {
        var options = TestNodes.NodeOptionsFor("node-" + index, _options.Shards, _options.TickRate, _nodes.Count == 0 ? null : _nodes[0]);
        options.TimeProvider = Time;
        if (index == 0 && _options.ClientConnections) options.Features.Add(new ClientConnections(new ClientConnectionOptions()));
        _options.ConfigureNode?.Invoke(index, options);

        var node = new Node(options);
        await node.StartAsync().ConfigureAwait(false);
        _nodes.Add(node);
        return node;
    }
}
