using EasyEcs;

namespace DaServer.Simulation;

/// <summary>
/// What a swarm measures: a latency histogram and named counters, shared by every loop (systems reach it through the
/// <see cref="Measurements"/> singleton). <see cref="Measuring"/> marks the window that counts.
/// </summary>
public sealed class Meter
{
    private readonly Lock _gate = new();
    private readonly List<Counter> _counters = [];

    public Histogram Latency { get; } = new();
    public Counter Disconnects { get; }
    public volatile bool Measuring;

    public Meter() => Disconnects = Counter("disconnects");

    /// <summary>The counter named <paramref name="name"/>, reported with the others (get it at setup: counting allocates nothing).</summary>
    public Counter Counter(string name)
    {
        lock (_gate)
        {
            foreach (var existing in _counters)
                if (existing.Name == name) return existing;
            var counter = new Counter(name);
            _counters.Add(counter);
            return counter;
        }
    }

    public IReadOnlyList<Counter> Counters
    {
        get
        {
            lock (_gate) return [.. _counters];
        }
    }

    /// <summary>Start the measured window: latencies and counters start from zero.</summary>
    public void Start()
    {
        Latency.Clear();
        foreach (var counter in Counters) counter.Reset();
        Measuring = true;
    }
}

/// <summary>A count shared by every loop.</summary>
public sealed class Counter(string name)
{
    private long _value;

    public string Name { get; } = name;
    public long Value => Interlocked.Read(ref _value);

    public void Add(long n = 1) => Interlocked.Add(ref _value, n);

    internal void Reset() => Interlocked.Exchange(ref _value, 0);

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
