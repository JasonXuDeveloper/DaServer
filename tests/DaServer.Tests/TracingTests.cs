using System.Collections.Concurrent;
using System.Diagnostics;

namespace DaServer.Tests;

/// <summary>
/// Distributed tracing (A6): a handler runs in a span, child of whatever sent its message on any node, and only inside a trace
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
    public async Task AHandlersSpanIsTheChildOfItsSendersOnAnyNode()
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
            await nodes[0].Ask(new DriverId(remote), new Drive(DriveKind.Add, local, 0, 1, 3)); // three asks, one per answer
        }

        List<Activity> Spans() => [.. stopped.Where(s => s.TraceId == trace && s.Source.Name == "DaServer")];
        // The drive, its three adds and the three answers: an answer continues the trace of the handler that asked.
        await Eventually(() => Spans().Count == 7, "every handler's span ended");
        var drive = Spans().Single(static s => s.OperationName == nameof(Drive));
        Assert.That((drive.ParentSpanId, drive.Kind), Is.EqualTo((request, ActivityKind.Server)), "the remote handler continues the request's trace");
        Assert.That(drive.GetTagItem("daserver.handler"), Is.EqualTo(nameof(Driver)));
        var adds = Spans().Where(static s => s.OperationName == nameof(Add)).ToList();
        Assert.That(adds, Has.Count.EqualTo(3));
        var asked = adds.Select(static s => s.ParentSpanId).ToList();
        Assert.That(asked[0], Is.EqualTo(drive.SpanId), "the first ask is the drive's");
        var answers = Spans().Where(s => s != drive && !adds.Contains(s)).ToList();
        Assert.That(answers, Has.Count.EqualTo(3), "each answer runs in a span of the trace");
        Assert.That(answers.Select(static s => s.ParentSpanId), Is.SubsetOf(adds.Select(static s => s.SpanId).Append(drive.SpanId)),
            "an answer is the child of the ask it answers");
        Assert.That(asked.Skip(1), Is.SubsetOf(answers.Select(static s => s.SpanId)), "the later asks are their answers'");
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
