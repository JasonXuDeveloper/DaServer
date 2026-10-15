using EasyEcs;
using Sample.Client;
using Sample.Contracts;

namespace Sample.Bot;

/// <summary>What the measurement last saw of a player's realm status, and how many arrived while measuring.</summary>
public record struct Measured(long Tick, long Received, long InWindow) : IComponent;

/// <summary>
/// After the client handled its pushes, measures each realm status it received: the latency from the game node's
/// system to the client's handler, and gaps (a missed or reordered broadcast). Measurements are the process's, not a
/// world's: <see cref="Stats"/> is shared by every loop.
/// </summary>
[UpdateInGroup<ClientPushes>]
public sealed partial class MeasureSystem : EcsSystem
{
    public override int Priority => 10; // after PushSystem

    private const double TickMs = 1000.0 / Timing.TickRate;

    [ForEach]
    private static void Measure(in Realm realm, ref Measured seen)
    {
        long arrived = realm.Received - seen.Received;
        if (arrived == 0) return;
        if (Stats.Measuring)
        {
            if (seen.Tick != 0 && realm.Tick != seen.Tick + arrived * Timing.RealmStatusEveryTicks) Stats.Gap(seen.Tick, realm.Tick);
            // Statuses handled in the same pass: the earlier ones were sent a broadcast interval apart before the last.
            double latest = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - realm.ServerTimeTicks).TotalMilliseconds;
            for (long i = 0; i < arrived; i++) Stats.Latency.Add(latest + i * Timing.RealmStatusEveryTicks * TickMs);
        }

        seen = new Measured(realm.Tick, realm.Received, seen.InWindow + (Stats.Measuring ? arrived : 0));
    }
}

/// <summary>The process's measurements (every loop adds to them).</summary>
public static class Stats
{
    public static readonly Histogram Latency = new();
    public static long Gaps, Disconnects;
    public static volatile bool Measuring;

    public static void Gap(long previous, long tick)
    {
        if (Interlocked.Increment(ref Gaps) <= 5) Console.WriteLine($"gap: realm tick {previous} then {tick}");
    }

    public static void Start()
    {
        Latency.Clear();
        Interlocked.Exchange(ref Gaps, 0);
        Measuring = true;
    }
}

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

    public Histogram Snapshot()
    {
        var copy = new Histogram();
        for (int i = 0; i < _buckets.Length; i++) copy._buckets[i] = Volatile.Read(ref _buckets[i]);
        copy.Summarize();
        return copy;
    }

    /// <summary>What <paramref name="later"/> recorded after this snapshot.</summary>
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
