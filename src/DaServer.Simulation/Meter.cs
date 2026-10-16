using System.Runtime.InteropServices;
using EasyEcs;

namespace DaServer.Simulation;

/// <summary>How a counter is reported and judged.</summary>
public enum CounterKind
{
    /// <summary>A total, reported as is.</summary>
    Total,

    /// <summary>A rate: reported per second of each interval.</summary>
    Rate,

    /// <summary>Something that must not happen while measured: any count fails the run.</summary>
    Failure,
}

/// <summary>
/// What a swarm measures: a latency histogram and named counters, shared by every loop (systems reach it through the
/// <see cref="Measurements"/> singleton, message handlers hold it). <see cref="Measuring"/> marks the window that counts.
/// </summary>
public sealed class Meter
{
    private readonly Lock _gate = new();
    private Counter[] _counters = [];

    public Meter() => Disconnects = Counter("disconnects", CounterKind.Failure);

    public Histogram Latency { get; } = new();
    public Counter Disconnects { get; }
    public volatile bool Measuring;

    /// <summary>The counter named <paramref name="name"/> (get it at setup: counting allocates nothing).</summary>
    public Counter Counter(string name, CounterKind kind = CounterKind.Total)
    {
        lock (_gate)
        {
            foreach (var existing in _counters)
                if (existing.Name == name) return existing;
            var counter = new Counter(name, kind);
            _counters = [.. _counters, counter];
            return counter;
        }
    }

    public IReadOnlyList<Counter> Counters => Volatile.Read(ref _counters);

    /// <summary>Start the measured window: latencies and counters start from zero.</summary>
    public void Start()
    {
        Latency.Clear();
        foreach (var counter in Counters) counter.Reset();
        Measuring = true;
    }
}

/// <summary>A count shared by every loop, striped so that loops counting at once do not contend.</summary>
public sealed class Counter(string name, CounterKind kind)
{
    private const int Stripes = 16;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Stripe
    {
        [FieldOffset(64)] public long Value;
    }

    private readonly Stripe[] _stripes = new Stripe[Stripes];

    public string Name { get; } = name;
    public CounterKind Kind { get; } = kind;

    public long Value
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Stripes; i++) total += Volatile.Read(ref _stripes[i].Value);
            return total;
        }
    }

    public void Add(long n = 1) => Interlocked.Add(ref _stripes[Environment.CurrentManagedThreadId & (Stripes - 1)].Value, n);

    internal void Reset()
    {
        for (int i = 0; i < Stripes; i++) Interlocked.Exchange(ref _stripes[i].Value, 0);
    }

    public override string ToString() => $"{Name}={Value}";
}

/// <summary>The swarm's meter, as a singleton of every loop's world.</summary>
public readonly record struct Measurements(Meter Meter) : ISingletonComponent;

/// <summary>Lock-free latency histogram: 0.1 ms buckets up to 5 s.</summary>
public sealed class Histogram
{
    private const double BucketMs = 0.1;
    private readonly long[] _buckets = new long[50_001];

    public long Count { get; private set; }
    public double Max { get; private set; }

    public void Add(double ms) =>
        Interlocked.Increment(ref _buckets[Math.Clamp((int)(ms / BucketMs), 0, _buckets.Length - 1)]);

    public void Clear() => Array.Clear(_buckets);

    /// <summary>A copy of what was recorded so far (with <see cref="Count"/>, <see cref="Max"/> and percentiles).</summary>
    public Histogram Snapshot()
    {
        var copy = new Histogram();
        for (int i = 0; i < _buckets.Length; i++) copy._buckets[i] = Volatile.Read(ref _buckets[i]);
        copy.Summarize();
        return copy;
    }

    /// <summary>What <paramref name="later"/> (a snapshot) recorded after this one.</summary>
    public Histogram Until(Histogram later)
    {
        var interval = new Histogram();
        for (int i = 0; i < _buckets.Length; i++) interval._buckets[i] = later._buckets[i] - _buckets[i];
        interval.Summarize();
        return interval;
    }

    public double Percentile(double p)
    {
        long target = (long)Math.Ceiling(Count * p), seen = 0;
        for (int i = 0; i < _buckets.Length; i++)
            if ((seen += _buckets[i]) >= target && target > 0)
                return (i + 1) * BucketMs;
        return 0;
    }

    public override string ToString() =>
        $"p50={Percentile(0.50),6:F1}ms p99={Percentile(0.99),6:F1}ms max={Max,6:F1}ms";

    private void Summarize()
    {
        Count = 0;
        Max = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            Count += _buckets[i];
            if (_buckets[i] != 0) Max = (i + 1) * BucketMs;
        }
    }
}
