using System.Collections.Generic;
using System.Diagnostics.Metrics;
using DaServer.Runtime;

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
            description: "Active actor activations");
        Meter.CreateObservableGauge("daserver.gateway.sessions", () => Sample(static n => n.SessionCount),
            description: "Connected client sessions");
        Meter.CreateObservableCounter("daserver.shard.rejected", () => Sample(static n => n.Rejected),
            description: "Calls and events refused because a shard or actor mailbox was full");
    }

    public static void Track(Node node)
    {
        lock (Nodes) Nodes.Add(node);
    }

    private static IEnumerable<Measurement<T>> Sample<T>(System.Func<Node, T> read) where T : struct
    {
        Node[] nodes;
        lock (Nodes) nodes = Nodes.ToArray();
        foreach (var node in nodes)
            yield return new Measurement<T>(read(node), new KeyValuePair<string, object?>("node", node.Options.Name));
    }
}
