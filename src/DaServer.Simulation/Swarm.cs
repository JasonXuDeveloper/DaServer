using System.Net;
using DaServer.Client;
using DaServer.Concurrency;
using DaServer.Diagnostics;
using DaServer.Protocol;
using DaServer.Rpc;
using EasyEcs;

namespace DaServer.Simulation;

/// <summary>How a swarm runs.</summary>
public sealed class SwarmOptions
{
    /// <summary>Client loops (threads), each with its own world; users are spread over them.</summary>
    public int Loops { get; init; } = Environment.ProcessorCount;

    /// <summary>Simulation ticks per second of every loop (the servers' rate, for prediction).</summary>
    public int TickRate { get; init; } = 20;

    /// <summary>Users starting at once (connecting, logging in): a swarm ramps up instead of stampeding.</summary>
    public int MaxStarting { get; init; } = 256;

    /// <summary>Run on every loop pass: the client's arrival handling (pushes as they come).</summary>
    public Action<World>? OnPass { get; init; }

    /// <summary>Set up each loop's world (systems, singletons; counters of the swarm's meter), on its thread, before any user starts.</summary>
    public Action<World, SwarmMeter>? Configure { get; init; }
}

/// <summary>One simulated user being started: its index, its client and the world it lives in.</summary>
public readonly record struct SimulatedUser(int Index, ClusterClient Client, World World)
{
    /// <summary>Connect through <paramref name="endpoints"/>, retrying while the cluster is not reachable yet.</summary>
    public Task ConnectAsync(IReadOnlyList<DnsEndPoint> endpoints) => Connect(Client, endpoints, static (c, e) => c.ConnectAsync(e));

    /// <summary>Connect over WebSocket (as browsers do), retrying while the cluster is not reachable yet.</summary>
    public Task ConnectAsync(Uri webSocket) => Connect(Client, webSocket, static (c, uri) => c.ConnectAsync(uri));

    private static Task Connect<T>(ClusterClient client, T target, Func<ClusterClient, T, ValueTask> connect) =>
        Swarm.Retry(async () =>
        {
            await connect(client, target);
            return true;
        }, static e => e is not RpcException);
}

/// <summary>
/// Thousands of simulated users against a cluster: a user is an entity in one of several client worlds (one
/// <see cref="ClientLoop"/> per core), driven by systems (scripted or planned, <see cref="Planning"/>) exactly as a real
/// client's UI would drive it. The swarm starts them in waves, waits for a condition, measures a window (a report line
/// every interval) and judges it: <see cref="SwarmCounterKind.Failure"/> counters, the latency limit and the scenario's own
/// checks (<see cref="SwarmResult.Expect"/>).
/// </summary>
public sealed class Swarm : IAsyncDisposable
{
    private readonly SwarmOptions _options;
    private readonly ClientLoop[] _loops;
    private ClusterClient[] _clients = [];
    private int _disconnectsLogged;

    public Swarm(SwarmOptions options)
    {
        _options = options;
        _loops = new ClientLoop[Math.Max(1, options.Loops)];
        var meter = Meter;
        for (int i = 0; i < _loops.Length; i++)
        {
            _loops[i] = new ClientLoop("swarm-" + i, options.TickRate, options.OnPass);
            _ = _loops[i].Run(world =>
            {
                world.AddSingleton<SwarmMetrics>() = new SwarmMetrics(meter);
                options.Configure?.Invoke(world, meter);
            });
        }
    }

    public SwarmMeter Meter { get; } = new();
    public IReadOnlyList<ClientLoop> Loops => _loops;

    /// <summary>
    /// Create <paramref name="users"/> clients (<paramref name="client"/>) and start each with <paramref name="start"/>
    /// on its loop (connect, log in, add its components), at most <see cref="SwarmOptions.MaxStarting"/> at once.
    /// Disconnects count in <see cref="Meter"/>.
    /// </summary>
    public async Task StartAsync(int users, Func<ClusterClient> client, Func<SimulatedUser, Task> start)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        _clients = new ClusterClient[users];
        await Parallel.ForAsync(0, users, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxStarting }, async (i, _) =>
        {
            var c = _clients[i] = client();
            c.Disconnected += e =>
            {
                Meter.Disconnects.Add();
                if (Interlocked.Increment(ref _disconnectsLogged) <= 5) Console.WriteLine($"user {i} disconnected: {e?.Message}");
            };
            var loop = _loops[i % _loops.Length];
            await loop.Run(async () =>
            {
                await start(new SimulatedUser(i, c, loop.World));
                return true;
            });
        });
        Console.WriteLine($"{users} users started in {started.Elapsed.TotalSeconds:F1}s on {_loops.Length} client loops");
    }

    /// <summary>The sum of <paramref name="count"/> over every world, each read on its loop.</summary>
    public async Task<long> SumAsync(Func<World, long> count)
    {
        long total = 0;
        foreach (var loop in _loops) total += await loop.Run(() => Task.FromResult(count(loop.World)));
        return total;
    }

    /// <summary>Wait until <paramref name="pending"/> is 0, or <paramref name="timeout"/>; false on timeout.</summary>
    public async Task<bool> WaitAsync(Func<Task<long>> pending, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (await pending() > 0)
        {
            if (watch.Elapsed >= timeout) return false;
            await Task.Delay(100);
        }

        return true;
    }

    /// <summary>
    /// The measured window: counters and latencies start from zero, a line every <paramref name="every"/> (latency
    /// of the interval, rates per second, totals and failures), until <paramref name="duration"/>. The result holds the
    /// window's latencies, what the simulation cost the process, and the failures judged so far.
    /// </summary>
    public Task<SwarmResult> MeasureAsync(TimeSpan duration, TimeSpan every, double maxP99Ms = double.PositiveInfinity) =>
        MeasureAsync(duration, null, every, maxP99Ms);

    /// <summary>
    /// <see cref="MeasureAsync(TimeSpan, TimeSpan, double)"/> until <paramref name="done"/> completes (a scenario that
    /// ends by itself: a crowd that has bought), with a line every <paramref name="every"/> meanwhile.
    /// </summary>
    public Task<SwarmResult> MeasureUntilAsync(Task done, TimeSpan every, double maxP99Ms = double.PositiveInfinity) =>
        MeasureAsync(Timeout.InfiniteTimeSpan, done, every, maxP99Ms);

    private async Task<SwarmResult> MeasureAsync(TimeSpan duration, Task? done, TimeSpan every, double maxP99Ms)
    {
        var before = ProcessCost.Now();
        Meter.Start();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var window = Meter.Latency.Snapshot();
        var counts = Meter.Counters.Select(static c => c.Value).ToArray();
        var last = TimeSpan.Zero;
        while (done == null ? clock.Elapsed < duration : !done.IsCompleted)
        {
            var wait = done == null ? duration - clock.Elapsed : every;
            var interval = Task.Delay(wait < every ? wait : every);
            await (done == null ? interval : Task.WhenAny(interval, done));
            var now = Meter.Latency.Snapshot();
            double seconds = (clock.Elapsed - last).TotalSeconds;
            var counters = Meter.Counters;
            var line = new System.Text.StringBuilder($"t={clock.Elapsed.TotalSeconds,4:F0}s latency {window.Until(now)}");
            for (int i = 0; i < counters.Count; i++)
            {
                long value = counters[i].Value, previous = i < counts.Length ? counts[i] : 0;
                line.Append(counters[i].Kind == SwarmCounterKind.Rate
                    ? $" {counters[i].Name}/s={(value - previous) / seconds:F0}"
                    : $" {counters[i].Name}={value}");
            }

            Console.WriteLine(line);
            (window, counts, last) = (now, counters.Select(static c => c.Value).ToArray(), clock.Elapsed);
        }

        Meter.Measuring = false;
        var result = new SwarmResult(Meter.Latency.Snapshot(), ProcessCost.Now() - before);
        foreach (var counter in Meter.Counters)
            result.Expect(counter.Kind != SwarmCounterKind.Failure || counter.Value == 0, counter.ToString());
        double p99 = result.Latency.Percentile(0.99);
        result.Expect(p99 <= maxP99Ms, $"p99 latency {p99:F1}ms > {maxP99Ms}ms");
        return result;
    }

    /// <summary>
    /// Retry an idempotent <paramref name="call"/> while it fails with what <paramref name="transient"/> accepts
    /// (default: the cluster is starting or momentarily full: NotHosted, Timeout, Overloaded), after the wait the refusal
    /// names (<see cref="RpcException.RetryAfter"/>) and a jittered backoff (up to half a second, doubling to 5 s), so a
    /// crowd refused together does not come back together.
    /// </summary>
    public static async Task<T> Retry<T>(Func<Task<T>> call, Func<Exception, bool>? transient = null, int attempts = 60)
    {
        var backoff = new Backoff(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception e) when (attempt < attempts && (transient ?? Starting)(e))
            {
                var hint = e is RpcException rpc ? rpc.RetryAfter : TimeSpan.Zero;
                await Task.Delay(hint + backoff.Next());
            }
        }
    }

    private static bool Starting(Exception e) => e is RpcException { Status: RpcStatus.NotHosted or RpcStatus.Timeout or RpcStatus.Overloaded };

    public async ValueTask DisposeAsync()
    {
        // Every client says goodbye at once: one round trip for all, not one per client and core.
        var goodbyes = new Task[_clients.Length];
        for (int i = 0; i < goodbyes.Length; i++) goodbyes[i] = _clients[i]?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        await Task.WhenAll(goodbyes);
        foreach (var loop in _loops) loop.Dispose();
    }
}
