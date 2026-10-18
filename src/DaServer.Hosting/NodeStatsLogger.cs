using DaServer.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DaServer.Hosting;

/// <summary>
/// Logs one line of process and node figures per interval (CPU, allocation rate, GCs, pauses, actors, queue, rejected,
/// and every gauge the node's features report, such as sessions): the numbers a load test needs, readable from any
/// console or container log without a metrics backend.
/// </summary>
internal sealed partial class NodeStatsLogger(Node node, ILogger<NodeStatsLogger> logger, TimeSpan interval) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        var last = ProcessCost.Now();
        var features = new System.Text.StringBuilder();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = ProcessCost.Now();
                var window = now - last;
                features.Clear();
                foreach (var (name, _, read) in node.Gauges) // daserver.clients.sessions -> sessions=...
                    features.Append(' ').Append(name, name.LastIndexOf('.') + 1, name.Length - name.LastIndexOf('.') - 1).Append('=').Append(read());
                Stats(logger, window.CpuShare * 100, window.AllocatedKBPerSecond, window.Gen0, window.Gen1, window.Gen2,
                    window.PausedShare * 100, GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024), node.ActorCount,
                    node.TotalQueueLength(), node.Dropped(DropReason.QueueFull) + node.Dropped(DropReason.MailboxFull), node.Dropped(DropReason.Expired),
                    features.ToString());
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "stats: cpu={Cpu:F0}% alloc={Alloc:F0}KB/s gc0/1/2={Gen0}/{Gen1}/{Gen2} " +
        "pause={Pause:F2}% heap={Heap}MB actors={Actors} queued={Queued} rejected={Rejected} expired={Expired}{Features}")]
    private static partial void Stats(ILogger logger, double cpu, double alloc, int gen0, int gen1, int gen2, double pause, long heap,
        long actors, long queued, long rejected, long expired, string features);
}
