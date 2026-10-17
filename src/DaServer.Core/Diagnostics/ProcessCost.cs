#if NET
using System.Diagnostics;

namespace DaServer.Diagnostics;

/// <summary>What the process spent: CPU, managed allocations and collections per generation (over a window: a difference).</summary>
public readonly record struct ProcessCost(TimeSpan Elapsed, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2, TimeSpan GcPaused)
{
    public static ProcessCost Now()
    {
        using var process = Process.GetCurrentProcess();
        return new ProcessCost(Stopwatch.GetElapsedTime(0), process.TotalProcessorTime, GC.GetTotalAllocatedBytes(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
    }

    public static ProcessCost operator -(ProcessCost a, ProcessCost b) =>
        new(a.Elapsed - b.Elapsed, a.Cpu - b.Cpu, a.Allocated - b.Allocated, a.Gen0 - b.Gen0, a.Gen1 - b.Gen1, a.Gen2 - b.Gen2,
            a.GcPaused - b.GcPaused);

    private double Seconds => Math.Max(Elapsed.TotalSeconds, 1e-9);

    /// <summary>CPU used as a share of all cores (1 = every core busy for the whole window).</summary>
    public double CpuShare => Cpu.TotalSeconds / Seconds / Environment.ProcessorCount;

    public double AllocatedKBPerSecond => Allocated / Seconds / 1024;

    /// <summary>Share of the window the runtime was paused for collections.</summary>
    public double PausedShare => GcPaused.TotalSeconds / Seconds;

    public override string ToString() =>
        $"cpu={CpuShare:P0} of {Environment.ProcessorCount} cores, allocated={AllocatedKBPerSecond:F0} KB/s, " +
        $"gc gen0/1/2={Gen0}/{Gen1}/{Gen2} paused {GcPaused.TotalMilliseconds:F0} ms";
}
#endif
