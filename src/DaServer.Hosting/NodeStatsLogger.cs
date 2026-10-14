using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DaServer.Hosting;

/// <summary>
/// Logs one line of process and node figures per interval (CPU, allocation rate, GCs, pauses, sessions, actors, queue,
/// rejected): the numbers a load test needs, readable from any console or container log without a metrics backend.
/// </summary>
internal sealed class NodeStatsLogger(Node node, ILogger<NodeStatsLogger> logger, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        var process = Process.GetCurrentProcess();
        var last = Sample.Take(process);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = Sample.Take(process);
                double seconds = (now.Time - last.Time).TotalSeconds;
                logger.LogInformation(
                    "stats: cpu={Cpu:F0}% alloc={Alloc:F0}KB/s gc0/1/2={Gen0}/{Gen1}/{Gen2} pause={Pause:F2}% " +
                    "heap={Heap}MB sessions={Sessions} actors={Actors} queued={Queued} rejected={Rejected}",
                    (now.Cpu - last.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100,
                    (now.Allocated - last.Allocated) / seconds / 1024,
                    now.Gen0 - last.Gen0, now.Gen1 - last.Gen1, now.Gen2 - last.Gen2,
                    (now.Pause - last.Pause).TotalSeconds / seconds * 100,
                    GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024),
                    node.SessionCount, node.ActorCount, node.TotalQueueLength(), node.Rejected);
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct Sample(TimeSpan Time, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2, TimeSpan Pause)
    {
        public static Sample Take(Process process)
        {
            process.Refresh();
            return new Sample(Stopwatch.GetElapsedTime(0), process.TotalProcessorTime, GC.GetTotalAllocatedBytes(),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
        }
    }
}
