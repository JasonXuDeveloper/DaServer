using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DaServer.Hosting;

/// <summary>
/// A node's instruments on the "DaServer" meter (OpenTelemetry / dotnet-counters / Aspire dashboard), created through
/// the host's <see cref="IMeterFactory"/> while the node runs. Observable instruments are sampled by the collector, so
/// the hot path pays nothing; every measurement carries the node's name.
/// </summary>
public sealed class NodeMetrics : IDisposable
{
    public const string MeterName = "DaServer";

    private readonly Meter _meter;
    private readonly Node _node;
    private readonly Histogram<double> _stopPhases;

    /// <summary>Report <paramref name="node"/> (once it started: its features have added their gauges).</summary>
    internal NodeMetrics(Node node, IMeterFactory meters)
    {
        _node = node;
        var tag = new KeyValuePair<string, object?>("node", node.Options.Name);
        _meter = meters.Create(new MeterOptions(MeterName) { Tags = [tag] }); // a meter of its own: the factory shares equal ones
        _meter.CreateObservableGauge("daserver.cluster.members", () => new Measurement<int>(node.View.UpMembers.Length, tag),
            description: "Up members seen by this node");
        _meter.CreateObservableGauge("daserver.shard.queue_length", () => new Measurement<long>(node.TotalQueueLength(), tag),
            description: "Messages waiting in shard mailboxes");
        _meter.CreateObservableGauge("daserver.actors", () => new Measurement<long>(node.ActorCount, tag),
            description: "Active loaded actors");
        _meter.CreateObservableCounter("daserver.messages.dropped", () => (Measurement<long>[])
            [
                Dropped(node, DropReason.QueueFull, "queue_full", tag), Dropped(node, DropReason.MailboxFull, "mailbox_full", tag),
                Dropped(node, DropReason.Expired, "expired", tag),
            ], description: "Messages dropped unrun: a full shard queue or actor mailbox, or a caller that had timed out");
        _meter.CreateObservableCounter("daserver.handler.failed", () => new Measurement<long>(node.Failed, tag),
            description: "Messages whose handler threw (poison messages show up here)");
        _meter.CreateObservableGauge("daserver.shard.busy_ratio", () => new Measurement<double>(BusyRatio(), tag),
            description: "Share of the shards' time spent working since the last collection (0-1): the default scaling metric");
        _meter.CreateObservableCounter("daserver.shard.tick_overruns", () => new Measurement<long>(node.TickOverruns, tag),
            description: "Ticks that started a whole interval late: shards that cannot keep their tick rate");
        _meter.CreateObservableGauge("daserver.shard.stalled", () => new Measurement<int>(node.StalledShards, tag),
            description: "Shards whose current pass runs longer than StallAfter (a blocked shard thread)");
        foreach (var (name, description, read) in node.Gauges)
            _meter.CreateObservableGauge(name, () => new Measurement<long>(read(), tag), description: description);
        _stopPhases = _meter.CreateHistogram<double>("daserver.stop.phase.duration", "ms",
            "Duration of each phase of a graceful stop (leave, drain, unload, close)");
        node.StopPhase += RecordStopPhase;
    }

    private readonly Lock _sampling = new();
    private long _sampledAt = Stopwatch.GetTimestamp(), _sampledIdle;

    /// <summary>
    /// The share of the shards' time spent working since the previous collection: one minus the time they slept waiting
    /// for work. CPU says the same for a node alone on its machine; this says it per node whatever shares the machine,
    /// and without the CPU time spent outside the shards (IO threads, GC).
    /// </summary>
    private double BusyRatio()
    {
        lock (_sampling)
        {
            long now = Stopwatch.GetTimestamp(), idle = _node.IdleTicks;
            long wall = (now - _sampledAt) * Math.Max(1, _node.ShardCount);
            double ratio = wall <= 0 ? 0 : 1 - (double)(idle - _sampledIdle) / wall;
            (_sampledAt, _sampledIdle) = (now, idle);
            return Math.Clamp(ratio, 0, 1);
        }
    }

    private void RecordStopPhase(string phase, TimeSpan elapsed) =>
        _stopPhases.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("node", _node.Options.Name));

    /// <summary>Stop reporting the node (after it stopped: its stop phases are recorded).</summary>
    public void Dispose()
    {
        _node.StopPhase -= RecordStopPhase;
        _meter.Dispose();
    }

    private static Measurement<long> Dropped(Node node, DropReason reason, string name, KeyValuePair<string, object?> tag) =>
        new(node.Dropped(reason), tag, new KeyValuePair<string, object?>("reason", name));
}
