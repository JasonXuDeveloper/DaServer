using System.Diagnostics.Metrics;

namespace DaServer.Hosting;

/// <summary>
/// Observable gauges on the "DaServer" meter (OpenTelemetry / dotnet-counters / Aspire dashboard). Observable
/// instruments are sampled by the collector, so the hot path pays nothing.
/// </summary>
public static class NodeMetrics
{
    public const string MeterName = "DaServer";

    private static readonly Meter Meter = new(MeterName);
    private static readonly List<Node> Nodes = new();

    static NodeMetrics()
    {
        Meter.CreateObservableGauge("daserver.cluster.members", () => Sample(static n => n.View.UpMembers.Length),
            description: "Up members seen by this node");
        Meter.CreateObservableGauge("daserver.shard.queue_length", () => Sample(static n => n.TotalQueueLength()),
            description: "Messages waiting in shard mailboxes");
        Meter.CreateObservableGauge("daserver.actors", () => Sample(static n => n.ActorCount),
            description: "Active loaded actors");
        Meter.CreateObservableCounter("daserver.shard.rejected", () => Sample(static n => n.Rejected),
            description: "Calls and events refused because a shard or actor mailbox was full");
        Meter.CreateObservableCounter("daserver.shard.expired", () => Sample(static n => n.Expired),
            description: "Requests dropped unrun because their caller had already timed out");
        Meter.CreateObservableGauge("daserver.shard.stalled", () => Sample(static n => n.StalledShards),
            description: "Shards whose current pass runs longer than StallAfter (a blocked shard thread)");
    }

    private static readonly HashSet<string> FeatureGauges = new();

    private static readonly Histogram<double> StopPhases = Meter.CreateHistogram<double>("daserver.stop.phase.duration", "ms",
        "Duration of each phase of a graceful stop (leave, drain, unload, close)");

    /// <summary>Report <paramref name="node"/> (once it started: its features have added their gauges).</summary>
    internal static void Track(Node node)
    {
        lock (Nodes)
        {
            Nodes.Add(node);
            node.StopPhase += static (phase, elapsed) =>
                StopPhases.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("phase", phase));
            foreach (var (name, description, _) in node.Gauges)
                if (FeatureGauges.Add(name))
                    Meter.CreateObservableGauge(name, () => SampleGauge(name), description: description);
        }
    }

    private static IEnumerable<Measurement<long>> SampleGauge(string name)
    {
        Node[] nodes;
        lock (Nodes) nodes = Nodes.ToArray();
        foreach (var node in nodes)
        foreach (var gauge in node.Gauges)
            if (gauge.Name == name)
                yield return new Measurement<long>(gauge.Read(), new KeyValuePair<string, object?>("node", node.Options.Name));
    }

    private static IEnumerable<Measurement<T>> Sample<T>(System.Func<Node, T> read) where T : struct
    {
        Node[] nodes;
        lock (Nodes) nodes = Nodes.ToArray();
        foreach (var node in nodes)
            yield return new Measurement<T>(read(node), new KeyValuePair<string, object?>("node", node.Options.Name));
    }
}
