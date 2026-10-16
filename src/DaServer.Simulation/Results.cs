namespace DaServer.Simulation;

/// <summary>A measured window: its latencies, what it cost the process, and what failed.</summary>
public sealed class SwarmResult(Histogram latency, ProcessCost cost)
{
    private readonly List<string> _failures = [];

    public Histogram Latency { get; } = latency;
    public ProcessCost Cost { get; } = cost;
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>Fail the run with <paramref name="failure"/> unless <paramref name="holds"/>.</summary>
    public SwarmResult Expect(bool holds, string failure)
    {
        if (!holds) _failures.Add(failure);
        return this;
    }

    /// <summary>Print the summary and the verdict (<c>E2E OK</c> or the failures); the process exit code.</summary>
    public int Report()
    {
        Console.WriteLine($"latency p50={Latency.Percentile(0.5):F1}ms p99={Latency.Percentile(0.99):F1}ms " +
                          $"p99.9={Latency.Percentile(0.999):F1}ms max={Latency.Max:F1}ms over {Latency.Count} samples");
        Console.WriteLine($"simulation cost: {Cost}");
        Console.WriteLine(_failures.Count == 0 ? "E2E OK" : "E2E FAILED: " + string.Join("; ", _failures));
        return _failures.Count == 0 ? 0 : 1;
    }
}

/// <summary>What the process spent: CPU, managed allocations and collections per generation (over a window: a difference).</summary>
public readonly record struct ProcessCost(TimeSpan Elapsed, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2)
{
    public static ProcessCost Now()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new ProcessCost(System.Diagnostics.Stopwatch.GetElapsedTime(0), process.TotalProcessorTime, GC.GetTotalAllocatedBytes(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
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
