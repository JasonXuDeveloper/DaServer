using DaServer.Diagnostics;
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
        var last = ProcessCost.Now();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = ProcessCost.Now();
                var window = now - last;
                logger.LogInformation(
                    "stats: cpu={Cpu:F0}% alloc={Alloc:F0}KB/s gc0/1/2={Gen0}/{Gen1}/{Gen2} pause={Pause:F2}% " +
                    "heap={Heap}MB sessions={Sessions} actors={Actors} queued={Queued} rejected={Rejected} expired={Expired}",
                    window.CpuShare * 100, window.AllocatedKBPerSecond, window.Gen0, window.Gen1, window.Gen2, window.PausedShare * 100,
                    GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024),
                    (node.Feature<DaServer.Clients.ClientConnections>()?.SessionCount ?? 0), node.ActorCount, node.TotalQueueLength(), node.Rejected, node.Expired);
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
