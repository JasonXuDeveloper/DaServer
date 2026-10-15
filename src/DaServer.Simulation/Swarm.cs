using DaServer.Client;
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
    public Action<World, Meter>? Configure { get; init; }
}

/// <summary>One simulated user being started: its index, its client and the world it lives in.</summary>
public readonly record struct SimulatedUser(int Index, ClusterClient Client, World World);

/// <summary>
/// Thousands of simulated users against a cluster: a user is an entity in one of several client worlds (one
/// <see cref="ClientLoop"/> per core), driven by systems (scripted or planned, <see cref="Planning"/>) exactly as a real
/// client's UI would drive it. The swarm starts them in waves, waits for a condition, measures a window and reads
/// per-world numbers on each loop's own thread; what counts as passing is the scenario's.
/// </summary>
public sealed class Swarm : IAsyncDisposable
{
    private readonly SwarmOptions _options;
    private readonly ClientLoop[] _loops;
    private ClusterClient[] _clients = [];

    public Swarm(SwarmOptions options)
    {
        _options = options;
        _loops = new ClientLoop[Math.Max(1, options.Loops)];
        for (int i = 0; i < _loops.Length; i++)
        {
            _loops[i] = new ClientLoop("swarm-" + i, options.TickRate, options.OnPass);
            var meter = Meter;
            _ = _loops[i].Run(world =>
            {
                world.AddSingleton<Measurements>() = new Measurements(meter);
                options.Configure?.Invoke(world, meter);
            });
        }
    }

    public Meter Meter { get; } = new();
    public IReadOnlyList<ClientLoop> Loops => _loops;

    /// <summary>
    /// Create <paramref name="users"/> clients (<paramref name="client"/>) and start each with <paramref name="start"/>
    /// on its loop (connect, log in, add its components), at most <see cref="SwarmOptions.MaxStarting"/> at once.
    /// Disconnects count in <see cref="Meter"/>.
    /// </summary>
    public async Task StartAsync(int users, Func<ClusterClient> client, Func<SimulatedUser, Task> start)
    {
        _clients = new ClusterClient[users];
        await Parallel.ForAsync(0, users, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxStarting }, async (i, _) =>
        {
            var c = _clients[i] = client();
            c.Disconnected += _ => Meter.Disconnects.Add();
            var loop = _loops[i % _loops.Length];
            await loop.Run(async () =>
            {
                await start(new SimulatedUser(i, c, loop.World));
                return true;
            });
        });
    }

    /// <summary>The sum of <paramref name="count"/> over every world, each read on its loop.</summary>
    public async Task<long> SumAsync(Func<World, long> count)
    {
        long total = 0;
        foreach (var loop in _loops) total += await loop.Run(() => Task.FromResult(count(loop.World)));
        return total;
    }

    /// <summary>Wait until <paramref name="pending"/> sums to 0 over every world, or <paramref name="timeout"/>; false on timeout.</summary>
    public async Task<bool> WaitAsync(Func<World, long> pending, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (await SumAsync(pending) > 0)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100);
        }

        return true;
    }

    /// <summary>
    /// The measured window: <see cref="Meter.Start"/>, then <paramref name="report"/> every
    /// <paramref name="every"/> with the latencies of that interval and its length, until <paramref name="duration"/>.
    /// Returns what the simulation itself cost the process meanwhile.
    /// </summary>
    public async Task<ProcessCost> MeasureAsync(TimeSpan duration, TimeSpan every, Func<Histogram, TimeSpan, Task> report)
    {
        var before = ProcessCost.Now();
        Meter.Start();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var window = Meter.Latency.Snapshot();
        var last = TimeSpan.Zero;
        while (clock.Elapsed < duration)
        {
            var wait = duration - clock.Elapsed;
            await Task.Delay(wait < every ? wait : every);
            var now = Meter.Latency.Snapshot();
            await report(window.Until(now), clock.Elapsed - last);
            (window, last) = (now, clock.Elapsed);
        }

        Meter.Measuring = false;
        return ProcessCost.Now() - before;
    }

    /// <summary>
    /// Retry <paramref name="call"/> while it fails with what <paramref name="transient"/> accepts (a cluster still
    /// starting), every half second, up to <paramref name="attempts"/> times.
    /// </summary>
    public static async Task<T> Retry<T>(Func<Task<T>> call, Func<Exception, bool> transient, int attempts = 60)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception e) when (attempt < attempts && transient(e))
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Parallel.ForEachAsync(_clients, static (c, _) => c?.DisposeAsync() ?? default);
        foreach (var loop in _loops) loop.Dispose();
    }
}

/// <summary>What the process spent: CPU, managed allocations and collections per generation (over a window: a difference).</summary>
public readonly record struct ProcessCost(TimeSpan Elapsed, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2)
{
    public static ProcessCost Now()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new ProcessCost(TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency),
            process.TotalProcessorTime, GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    public static ProcessCost operator -(ProcessCost a, ProcessCost b) =>
        new(a.Elapsed - b.Elapsed, a.Cpu - b.Cpu, a.Allocated - b.Allocated, a.Gen0 - b.Gen0, a.Gen1 - b.Gen1, a.Gen2 - b.Gen2);

    public override string ToString()
    {
        double seconds = Math.Max(Elapsed.TotalSeconds, 1e-9);
        return $"cpu={Cpu.TotalSeconds / seconds / Environment.ProcessorCount:P0} of {Environment.ProcessorCount} cores, " +
               $"allocated={Allocated / seconds / 1024:F0} KB/s, gc gen0/1/2={Gen0}/{Gen1}/{Gen2}";
    }
}
