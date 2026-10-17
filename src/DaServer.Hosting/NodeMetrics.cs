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
        _meter.CreateObservableCounter("daserver.shard.rejected", () => new Measurement<long>(node.Rejected, tag),
            description: "Calls and events refused because a shard or actor mailbox was full");
        _meter.CreateObservableCounter("daserver.handler.failed", () => new Measurement<long>(node.Failed, tag),
            description: "Messages whose handler threw (poison messages show up here)");
        _meter.CreateObservableCounter("daserver.shard.expired", () => new Measurement<long>(node.Expired, tag),
            description: "Requests dropped unrun because their caller had already timed out");
        _meter.CreateObservableGauge("daserver.shard.stalled", () => new Measurement<int>(node.StalledShards, tag),
            description: "Shards whose current pass runs longer than StallAfter (a blocked shard thread)");
        foreach (var (name, description, read) in node.Gauges)
            _meter.CreateObservableGauge(name, () => new Measurement<long>(read(), tag), description: description);
        _stopPhases = _meter.CreateHistogram<double>("daserver.stop.phase.duration", "ms",
            "Duration of each phase of a graceful stop (leave, drain, unload, close)");
        node.StopPhase += RecordStopPhase;
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
}
