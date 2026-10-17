using System.Runtime.InteropServices;
using EasyEcs;

namespace DaServer.Simulation;

/// <summary>How a counter is reported and judged.</summary>
public enum SwarmCounterKind
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
/// <see cref="SwarmMetrics"/> singleton, message handlers hold it). <see cref="Measuring"/> marks the window that counts.
/// </summary>
public sealed class SwarmMeter
{
    private readonly Lock _gate = new();
    private SwarmCounter[] _counters = [];

    public SwarmMeter() => Disconnects = Counter("disconnects", SwarmCounterKind.Failure);

    public LatencyHistogram Latency { get; } = new();
    public SwarmCounter Disconnects { get; }
    public volatile bool Measuring;

    /// <summary>The counter named <paramref name="name"/> (get it at setup: counting allocates nothing).</summary>
    public SwarmCounter Counter(string name, SwarmCounterKind kind = SwarmCounterKind.Total)
    {
        lock (_gate)
        {
            foreach (var existing in _counters)
                if (existing.Name == name) return existing;
            var counter = new SwarmCounter(name, kind);
            _counters = [.. _counters, counter];
            return counter;
        }
    }

    public IReadOnlyList<SwarmCounter> Counters => Volatile.Read(ref _counters);

    /// <summary>Start the measured window: latencies and counters start from zero.</summary>
    public void Start()
    {
        Latency.Clear();
        foreach (var counter in Counters) counter.Reset();
        Measuring = true;
    }
}

/// <summary>A count shared by every loop, striped so that loops counting at once do not contend.</summary>
public sealed class SwarmCounter(string name, SwarmCounterKind kind)
{
    private const int Stripes = 16;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Stripe
    {
        [FieldOffset(64)] public long Value;
    }

    private readonly Stripe[] _stripes = new Stripe[Stripes];

    public string Name { get; } = name;
    public SwarmCounterKind Kind { get; } = kind;

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
public readonly record struct SwarmMetrics(SwarmMeter Meter) : ISingletonComponent;

/// <summary>
/// Lock-free latency histogram with log-linear buckets: microseconds below 32 us, then 32 buckets per power of two
/// (at most 3% wide) up to hours, so 1,024 buckets (8 KB) cover every latency. Striped by thread like
/// <see cref="SwarmCounter"/>: loops recording the same latency at once do not fight over one bucket's cache line.
/// </summary>
public sealed class LatencyHistogram
{
    private const int SubBits = 5, Sub = 1 << SubBits, Buckets = 1024, Stripes = 16;
    private readonly long[] _buckets;
    private readonly int _stripes;

    public LatencyHistogram() : this(Stripes)
    {
    }

    private LatencyHistogram(int stripes)
    {
        _stripes = stripes;
        _buckets = new long[stripes * Buckets];
    }

    public long Count { get; private set; }
    public double Max { get; private set; }

    public void Add(double ms)
    {
        int stripe = Environment.CurrentManagedThreadId & (_stripes - 1);
        Interlocked.Increment(ref _buckets[stripe * Buckets + BucketOf((long)(ms * 1000))]);
    }

    public void Clear() => Array.Clear(_buckets);

    /// <summary>The bucket of <paramref name="us"/> microseconds.</summary>
    internal static int BucketOf(long us)
    {
        if (us < Sub) return us < 0 ? 0 : (int)us;
        int octave = System.Numerics.BitOperations.Log2((ulong)us); // us in [2^octave, 2^(octave+1))
        int index = (octave - SubBits + 1) * Sub + (int)((us >> (octave - SubBits)) & (Sub - 1));
        return Math.Min(index, Buckets - 1);
    }

    /// <summary>The upper bound of bucket <paramref name="index"/>, in milliseconds (what percentiles report).</summary>
    internal static double UpperMs(int index)
    {
        if (index < Sub) return (index + 1) / 1000.0;
        int octave = index / Sub + SubBits - 1;
        return ((long)(Sub + index % Sub + 1) << (octave - SubBits)) / 1000.0;
    }

    /// <summary>A copy of what was recorded so far (with <see cref="Count"/>, <see cref="Max"/> and percentiles).</summary>
    public LatencyHistogram Snapshot()
    {
        var copy = new LatencyHistogram(1);
        for (int i = 0; i < _buckets.Length; i++) copy._buckets[i % Buckets] += Volatile.Read(ref _buckets[i]);
        copy.Summarize();
        return copy;
    }

    /// <summary>What <paramref name="later"/> (a snapshot) recorded after this one (a snapshot).</summary>
    public LatencyHistogram Until(LatencyHistogram later)
    {
        var interval = new LatencyHistogram(1);
        for (int i = 0; i < Buckets; i++) interval._buckets[i] = later._buckets[i] - _buckets[i];
        interval.Summarize();
        return interval;
    }

    /// <summary>The latency <paramref name="p"/> of the recorded ones are at most (of a snapshot).</summary>
    public double Percentile(double p)
    {
        long target = (long)Math.Ceiling(Count * p), seen = 0;
        for (int i = 0; i < Buckets; i++)
            if ((seen += _buckets[i]) >= target && target > 0)
                return UpperMs(i);
        return 0;
    }

    public override string ToString() =>
        $"p50={Percentile(0.50),6:F1}ms p99={Percentile(0.99),6:F1}ms p99.9={Percentile(0.999),6:F1}ms max={Max,6:F1}ms";

    private void Summarize()
    {
        Count = 0;
        Max = 0;
        for (int i = 0; i < Buckets; i++)
        {
            Count += _buckets[i];
            if (_buckets[i] != 0) Max = UpperMs(i);
        }
    }
}

