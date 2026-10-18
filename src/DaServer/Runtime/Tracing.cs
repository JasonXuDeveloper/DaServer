using System.Diagnostics;

namespace DaServer.Runtime;

/// <summary>
/// Distributed tracing (A6): a turn whose message carries a trace context (<see cref="FrameExtensions.Trace"/>) runs in a
/// span of its own, child of the sender's, and the requests and messages it sends carry that span's context, so a REST
/// call and the actor calls it causes, on any nodes, are one trace. Spans exist only while something listens to
/// <see cref="SourceName"/> (OpenTelemetry when an exporter is configured); traces start where the application starts
/// them (ASP.NET Core requests, its own activities), never per message. Untraced, a send reads two references and a
/// turn one flag.
/// </summary>
internal static class Tracing
{
    public const string SourceName = "DaServer";

    public static readonly ActivitySource Source = new(SourceName);

    [ThreadStatic] private static Activity? t_pending;

    /// <summary>Put the running turn's trace context (else the thread's current activity: an HTTP request) on an outgoing frame.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Attach(FrameWriter frame)
    {
        if ((CallChain.Trace as Activity ?? Activity.Current) is { } span) Write(frame, span);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(FrameWriter frame, Activity span)
    {
        if (frame.TryGetExtension(FrameExtensions.Trace, out _)) return; // relayed: it keeps its sender's
        var data = frame.AddExtension(FrameExtensions.Trace, FrameExtensions.TraceSize);
        span.TraceId.CopyTo(data[..16]);
        span.SpanId.CopyTo(data.Slice(16, 8));
        data[24] = (byte)span.ActivityTraceFlags;
    }

    /// <summary>
    /// Start the span of <paramref name="handler"/> running <paramref name="frame"/> on <paramref name="target"/> when the
    /// frame carries a trace context and something listens; null otherwise. <paramref name="outer"/>: the thread's activity
    /// to restore after the dispatch (the span becomes current while the handler runs synchronously).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Activity? Start(FrameWriter frame, MessageHandler handler, object target, out Activity? outer)
    {
        outer = null;
        return Source.HasListeners() && frame.TryGetExtension(FrameExtensions.Trace, out var data) ? StartSpan(data, handler, target, out outer) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Activity? StartSpan(ReadOnlySpan<byte> data, MessageHandler handler, object target, out Activity? outer)
    {
        outer = Activity.Current;
        var parent = new ActivityContext(ActivityTraceId.CreateFromBytes(data[..16]), ActivitySpanId.CreateFromBytes(data.Slice(16, 8)),
            (ActivityTraceFlags)data[24], isRemote: true);
        var span = Source.StartActivity(handler.MessageType.Name, ActivityKind.Server, parent);
        if (span is { IsAllDataRequested: true })
        {
            span.SetTag("daserver.handler", target.GetType().Name);
            if (target is Actor actor) span.SetTag("daserver.key", actor.Key);
        }

        return span;
    }

    /// <summary>The dispatch of <paramref name="span"/>'s turn returned: it ends with the turn (<see cref="Pending"/>) or now.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Dispatched(Activity span, Activity? outer, bool pending, Exception? error)
    {
        if (error != null) span.SetStatus(ActivityStatusCode.Error, error.Message);
        if (pending) t_pending = span;
        else span.Stop();
        Activity.Current = outer;
    }

    /// <summary>The span of the turn whose dispatch just returned unfinished (taken by <see cref="TurnWatcher.Begin"/>).</summary>
    public static Activity? TakePending()
    {
        var span = t_pending;
        t_pending = null;
        return span;
    }

    /// <summary>A traced turn ended (<paramref name="error"/>: how it failed).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ended(Activity span, Exception? error)
    {
        if (error != null) span.SetStatus(ActivityStatusCode.Error, error.Message);
        span.Stop();
    }
}
