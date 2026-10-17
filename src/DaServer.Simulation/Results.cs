using DaServer.Diagnostics;

namespace DaServer.Simulation;

/// <summary>A measured window: its latencies, what it cost the process, and what failed.</summary>
public sealed class SwarmResult(LatencyHistogram latency, ProcessCost cost)
{
    private readonly List<string> _failures = [];

    public LatencyHistogram Latency { get; } = latency;
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
        Console.WriteLine($"latency {Latency} over {Latency.Count} samples");
        Console.WriteLine($"simulation cost: {Cost}");
        Console.WriteLine(_failures.Count == 0 ? "E2E OK" : "E2E FAILED: " + string.Join("; ", _failures));
        return _failures.Count == 0 ? 0 : 1;
    }
}
