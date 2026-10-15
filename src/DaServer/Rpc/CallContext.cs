using System.Threading.Tasks.Sources;

namespace DaServer.Rpc;

/// <summary>Where the response of an incoming request goes (a connection, the local pending table, ...).</summary>
public interface IReplySink
{
    /// <summary>
    /// Send <paramref name="response"/> (payload already written) as the response to <paramref name="requestId"/>.
    /// Takes ownership of <paramref name="response"/>.
    /// </summary>
    void SendReply(uint requestId, RpcStatus status, FrameWriter response);
}

/// <summary>A reply sink that writes the response frame to a stream of frames (a connection or a node link).</summary>
internal interface IFrameSink : IReplySink, IFrameSender
{
    void IReplySink.SendReply(uint requestId, RpcStatus status, FrameWriter response)
    {
        response.SetHeader(FrameKind.Response, requestId, 0, 0, status);
        Send(response.Frame);
        response.Dispose();
    }
}

internal static class ReplySinks
{
    /// <summary>Answer <paramref name="requestId"/> with an empty success.</summary>
    public static void SendOk(this IReplySink sink, uint requestId) =>
        sink.SendReply(requestId, RpcStatus.Ok, FrameWriter.Rent(FrameHeader.Size));

    /// <summary>Answer <paramref name="requestId"/> with an error <paramref name="status"/> and message.</summary>
    public static void SendError(this IReplySink sink, uint requestId, RpcStatus status, string? message)
    {
        var w = FrameWriter.Rent();
        var nino = w.Nino;
        NinoSerializer.Serialize(message, ref nino);
        sink.SendReply(requestId, status, w);
    }
}

/// <summary>
/// Per-message context passed to generated dispatchers. A small struct: no allocation per call.
/// Inside a handler, <see cref="Current"/> exposes it until the handler's first <c>await</c>.
/// </summary>
public readonly struct CallContext
{
    [ThreadStatic] private static CallContext _current;

    public readonly IReplySink? Sink;
    public readonly uint RequestId;

    /// <summary>Client session that originated the call (through a gateway), or default for server-internal calls.</summary>
    public readonly SessionRef CallerSession;

    public CallContext(IReplySink? sink, uint requestId, SessionRef caller)
    {
        Sink = sink;
        RequestId = requestId;
        CallerSession = caller;
    }

    /// <summary>Context of the call being dispatched on this thread (valid until the handler's first await).</summary>
    public static CallContext Current => _current;

    public bool ExpectsReply => RequestId != 0 && Sink != null;

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

    public void ReplyValue<T>(T value)
    {
        if (!ExpectsReply) return;
        var w = FrameWriter.Rent();
        try
        {
            var nino = w.Nino;
            NinoSerializer.Serialize(value, ref nino);
        }
        catch (Exception e)
        {
            w.Dispose();
            ReplyError(RpcStatus.Error, "Response serialization failed: " + e.Message);
            return;
        }

        Sink!.SendReply(RequestId, RpcStatus.Ok, w);
    }

    public void ReplyError(RpcStatus status, string? message)
    {
        if (ExpectsReply) Sink!.SendError(RequestId, status, message);
    }

    public void ReplyException(Exception e)
    {
        if (e is RpcException rpc)
            ReplyError(rpc.Status, rpc.Message);
        else
            ReplyError(RpcStatus.Error, e.GetType().Name + ": " + e.Message);
    }

    private static ValueTask AwaitReply<T>(ValueTask<T> task, CallContext context) => ReplyWatcher<T>.Watch(task, context);
}

/// <summary>
/// Replies once a handler's pending <see cref="ValueTask{TResult}"/> completes, and is itself the turn's task (what
/// <c>TurnWatcher</c> watches). Pooled per shard thread: rented when a request handler suspends and returned when the
/// turn's result is read (<see cref="IValueTaskSource.GetResult"/>), both on the shard thread. The continuation is a
/// delegate created once per watcher and registered without capturing a context, so replying allocates nothing
/// (an <c>async</c> method here would allocate a state machine per suspended call beyond the builder's small cache,
/// and resuming on the shard's context would allocate a continuation per await).
/// </summary>
internal sealed class ReplyWatcher<T> : IValueTaskSource
{
    [ThreadStatic] private static Stack<ReplyWatcher<T>>? _pool;

    private readonly Action _completed;
    private ManualResetValueTaskSourceCore<bool> _core;
    private ValueTask<T> _task;
    private CallContext _context;

    private ReplyWatcher()
    {
        _completed = Completed;
        _core.RunContinuationsAsynchronously = false; // TurnWatcher only posts to the shard: safe to run inline
    }

    public static ValueTask Watch(ValueTask<T> task, CallContext context)
    {
        var watcher = _pool is { Count: > 0 } pool ? pool.Pop() : new ReplyWatcher<T>();
        watcher._task = task;
        watcher._context = context;
        var turn = new ValueTask(watcher, watcher._core.Version);
        task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(watcher._completed);
        return turn;
    }

    /// <summary>Wherever the handler completed (normally its shard thread): reply, then end the turn.</summary>
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
        (_pool ??= new Stack<ReplyWatcher<T>>()).Push(this);
    }
}
