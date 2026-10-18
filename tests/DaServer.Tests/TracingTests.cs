using System.Collections.Concurrent;
using System.Diagnostics;

namespace DaServer.Tests;

/// <summary>
/// Distributed tracing (A6): a turn runs in a span, child of whatever sent its message on any node, and only inside a trace
/// someone started; the trace context travels in the frame's extension block, which readers that do not know an entry skip.
/// </summary>
public class TracingTests : ClusterTest
{
    private static readonly ActivitySource Test = new("DaServer.Tests.Tracing");

    [Test]
    public void ExtensionsSitBetweenThePayloadAndTheFixedTrailersWhateverTheOrderTheyAreAddedIn()
    {
        using var w = FrameWriter.Rent();
        w.Write(new Point(1, 2));
        var payload = w.Payload.ToArray();
        w.SetCallId(42);
        w.AddExtension(9, 3).Fill(7); // an entry no reader knows
        w.SetChain(5);
        w.AddExtension(FrameExtensions.Trace, FrameExtensions.TraceSize).Fill(1);
        w.SetOrigin(new Origin(3, 4));
        w.SetHeader(FrameKind.Message, 1, 2, 3);

        var copy = FrameWriter.CopyOf(w.Frame);
        foreach (var frame in new[] { w, copy })
        {
            Assert.That(frame.Payload.ToArray(), Is.EqualTo(payload), "the payload is unchanged");
            Assert.That((frame.CallId, frame.Chain, frame.Origin), Is.EqualTo((42u, 5L, new Origin(3, 4))), "the fixed trailers are read from the end");
            Assert.That(frame.TryGetExtension(FrameExtensions.Trace, out var trace) && trace.Length == FrameExtensions.TraceSize && trace[24] == 1, Is.True);
            Assert.That(frame.TryGetExtension(9, out var unknown) && unknown.SequenceEqual(new byte[] { 7, 7, 7 }), Is.True);
            Assert.That(frame.TryGetExtension(2, out _), Is.False, "an entry the frame lacks");
        }

        var header = FrameHeader.Read(w.Frame);
        Assert.That(FrameHeader.PayloadOf(in header, w.Frame).ToArray(), Is.EqualTo(payload), "a reader of the received frame");
        copy.Dispose();
    }

    [Test]
    public async Task ATurnsSpanIsTheChildOfItsSendersOnAnyNode()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var nodes = await StartCluster(2);
        long remote = nodes[0].KeyOwnedBy<CounterId>(nodes[1], 9100), local = nodes[0].KeyOwnedBy<CounterId>(nodes[0], 9200);

        ActivityTraceId trace;
        ActivitySpanId request;
        using (var root = Test.StartActivity("request")!)
        {
            (trace, request) = (root.TraceId, root.SpanId);
            await nodes[0].Ask(new CounterId(remote), new AddMany(new CounterId(local), 3));
        }

        List<Activity> Spans() => [.. stopped.Where(s => s.TraceId == trace && s.Source.Name == "DaServer")];
        await Eventually(() => Spans().Count == 104, "every turn's span ended");
        var turn = Spans().Single(static s => s.OperationName == nameof(AddMany));
        Assert.That((turn.ParentSpanId, turn.Kind), Is.EqualTo((request, ActivityKind.Server)), "the remote turn continues the request's trace");
        Assert.That(turn.GetTagItem("daserver.handler"), Is.EqualTo(nameof(Counter)));
        var adds = Spans().Where(static s => s.OperationName == nameof(Add)).ToList();
        Assert.That(adds, Has.Count.EqualTo(103));
        Assert.That(adds.Select(static s => s.ParentSpanId), Has.All.EqualTo(turn.SpanId), "every call the turn made, after each await too");
    }

    [Test]
    public async Task NoSpanStartsOutsideATrace()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var node = await Start("single");
        await node.Ask(new CounterId(9300), new AddVia(new CounterId(9301), 1));
        Assert.That(stopped.Where(static s => s.Source.Name == "DaServer"), Is.Empty, "a message outside a trace starts none");
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is "DaServer" or "DaServer.Tests.Tracing",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
