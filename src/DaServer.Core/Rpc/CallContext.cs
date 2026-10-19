using System.Threading.Tasks.Sources;

namespace DaServer.Rpc;

/// <summary>Where the reply of an incoming request goes (a connection, the local pending table, ...).</summary>
public interface IReplySink
{
    /// <summary>
    /// Send <paramref name="reply"/> (payload already written) as the reply to <paramref name="callId"/>.
    /// Takes ownership of <paramref name="reply"/>.
    /// </summary>
    void SendReply(uint callId, RpcStatus status, FrameWriter reply);
}

/// <summary>A reply sink that writes the reply frame to a stream of frames (a connection or a node link).</summary>
internal interface IFrameSink : IReplySink, IFrameSender
{
    void IReplySink.SendReply(uint callId, RpcStatus status, FrameWriter reply) => reply.SendReply(this, callId, status);
}

internal static class ReplySinks
{
    /// <summary>Answer <paramref name="callId"/> with an empty success.</summary>
    public static void SendOk(this IReplySink sink, uint callId) =>
        sink.SendReply(callId, RpcStatus.Ok, FrameWriter.Rent(FrameHeader.Size));

    /// <summary>
    /// Refuse <paramref name="callId"/> at the edge: a status-only reply with the time after which to retry (in the
    /// reply's key, for clients) when the sink writes frames.
    /// </summary>
    public static void SendRefusal(this IReplySink sink, uint callId, RpcStatus status, TimeSpan retryAfter)
    {
        var reply = FrameWriter.Rent(FrameHeader.Size);
        if (sink is IFrameSink frames && retryAfter > TimeSpan.Zero)
            reply.SendReply(frames, callId, status, (long)Math.Ceiling(retryAfter.TotalMilliseconds));
        else
            sink.SendReply(callId, status, reply);
    }

    /// <summary>
    /// Answer <paramref name="callId"/> with an error <paramref name="status"/>: a status-only frame (the runtime's
    /// refusals, allocation-free), or with a <paramref name="message"/> (a handler's error).
    /// </summary>
    public static void SendError(this IReplySink sink, uint callId, RpcStatus status, string? message = null)
    {
        if (message == null)
        {
            sink.SendReply(callId, status, FrameWriter.Rent(FrameHeader.Size));
            return;
        }

        var w = FrameWriter.Rent();
        w.Write(message);
        sink.SendReply(callId, status, w);
    }
}

/// <summary>
/// Per-message context passed to generated dispatchers. A small struct: no allocation per call.
/// Inside a handler, <see cref="Current"/> exposes it until the handler's first <c>await</c>.
/// </summary>
public readonly struct CallContext(IReplySink? replyTo, uint callId, Origin origin, long deadline = 0)
{
    [ThreadStatic] private static CallContext _current;

    /// <summary>Where the reply goes (null for one-way messages).</summary>
    public readonly IReplySink? ReplyTo = replyTo;

    public readonly uint CallId = callId;

    /// <summary>Who sent the message from outside the cluster (a client session), or default for messages sent inside it.</summary>
    public readonly Origin Origin = origin;

    /// <summary>When the caller stops waiting (<c>Platform.Milliseconds</c>), or 0 for one-way messages.</summary>
    public readonly long Deadline = deadline;

    /// <summary>Context of the call being dispatched on this thread (valid until the handler's first await).</summary>
    public static CallContext Current => _current;

    public bool ExpectsReply => CallId != 0 && ReplyTo != null;

    /// <summary>Runtime use: set the ambient context around a dispatch.</summary>
    public static CallContext Enter(in CallContext context)
    {
        var previous = _current;
        _current = context;
        return previous;
    }

    public static void Exit(in CallContext previous) => _current = previous;

    // ---------------- replies (used by generated dispatchers) ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask Reply<T>(ValueTask<T> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            ReplyValue(task.Result);
            return default;
        }

        return AwaitReply(task, this);
    }

    public void ReplyValue<T>(in T value)
    {
        if (!ExpectsReply) return;
        FrameWriter w;
        try
        {
            w = MessageWriter.Write(in value);
        }
        catch (Exception e)
        {
            ReplyError(RpcStatus.Error, "Reply serialization failed: " + e.Message);
            return;
        }

        ReplyTo!.SendReply(CallId, RpcStatus.Ok, w);
    }

    public void ReplyError(RpcStatus status, string? message)
    {
        if (ExpectsReply) ReplyTo!.SendError(CallId, status, message);
    }

    public void ReplyException(Exception e)
    {
        if (e is RpcException rpc)
            ReplyError(rpc.Status, rpc.Detail); // a status-only failure passes on status-only
        else
            ReplyError(RpcStatus.Error, e.GetType().Name + ": " + e.Message);
    }

    private static ValueTask AwaitReply<T>(ValueTask<T> task, CallContext context) => ReplyWatcher<T>.Watch(task, context);
}

/// <summary>
/// Replies once a client handler's pending <see cref="ValueTask{TResult}"/> completes (handlers of servers never wait,
/// ADR-081), and is itself the task its dispatcher returns. Pooled per thread: rented when a request handler suspends
/// and returned when that task's result is read (<see cref="IValueTaskSource.GetResult"/>). The continuation is a
/// delegate created once per watcher and registered without capturing a context, so replying allocates nothing (an
/// <c>async</c> method here would allocate a state machine per suspended call beyond the builder's small cache).
/// </summary>
internal sealed class ReplyWatcher<T> : IValueTaskSource
{
    private readonly Action _completed;
    private ManualResetValueTaskSourceCore<bool> _core;
    private ValueTask<T> _task;
    private CallContext _context;

    private ReplyWatcher()
    {
        _completed = Completed;
        _core.RunContinuationsAsynchronously = false; // its awaiter only reads the result: safe to run inline
    }

    public static ValueTask Watch(ValueTask<T> task, CallContext context)
    {
        var watcher = PerThread<ReplyWatcher<T>>.TryRent(out var pooled) ? pooled : new ReplyWatcher<T>();
        watcher._task = task;
        watcher._context = context;
        var pending = new ValueTask(watcher, watcher._core.Version);
        task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(watcher._completed);
        return pending;
    }

    /// <summary>Wherever the handler completed: reply, then complete the dispatcher's task.</summary>
    private void Completed()
    {
        var (task, context) = (_task, _context);
        _task = default;
        try
        {
            context.ReplyValue(task.GetAwaiter().GetResult());
        }
        catch (Exception e)
        {
            context.ReplyException(e);
            _core.SetException(e); // the dispatcher's task fails too: its caller logs it
            return;
        }

        _core.SetResult(true);
    }

    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    public void GetResult(short token)
    {
        _core.GetResult(token);
        _core.Reset();
        _context = default;
        PerThread<ReplyWatcher<T>>.Return(this);
    }
}
