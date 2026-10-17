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
public readonly struct CallContext
{
    [ThreadStatic] private static CallContext _current;

    /// <summary>Where the reply goes (null for one-way messages).</summary>
    public readonly IReplySink? ReplyTo;

    public readonly uint CallId;

    /// <summary>Who sent the message from outside the cluster (a client session), or default for messages sent inside it.</summary>
    public readonly Origin Origin;

    public CallContext(IReplySink? replyTo, uint callId, Origin origin)
    {
        ReplyTo = replyTo;
        CallId = callId;
        Origin = origin;
    }

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
            if (DeferredReply<T>.Used && DeferredReply<T>.TryTake(out var deferred)) // ReplyWhen: the turn ends now
                deferred.Start(this);
            else
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
            w.Write(value);
        }
        catch (Exception e)
        {
            w.Dispose();
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
            _core.SetException(e); // the turn fails too: the runtime counts it and applies the actor's OnError
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
        (_pool ??= new Stack<ReplyWatcher<T>>()).Push(this);
    }
}

/// <summary>
/// A reply sent once a task completes, after its handler's turn ended (<c>ShardObject.ReplyWhen</c>): the handler
/// returned at once, so its actor takes its next message while the task (an access change, a call, a save) is
/// pending. The handler leaves a prepared watcher in a thread-static slot that the generated dispatcher's
/// <see cref="CallContext.Reply{T}"/> takes right away on the same thread. Watchers are pooled per thread; replying
/// allocates nothing.
/// </summary>
internal abstract class DeferredReply<T>
{
    /// <summary>Whether any handler replied later with <typeparamref name="T"/>: until then replies skip the slot.</summary>
    internal static volatile bool Used;

    [ThreadStatic] private static DeferredReply<T>? _pending;

    private protected T Value = default!;
    private protected CallContext Context;

    /// <summary>Leave <paramref name="watcher"/> for the dispatcher (the handler's own return value is ignored).</summary>
    private protected static void Leave(DeferredReply<T> watcher, T reply)
    {
        if (_pending != null) throw new InvalidOperationException("ReplyWhen: one per handler, as what it returns (DAS011)");
        watcher.Value = reply;
        Used = true;
        _pending = watcher;
    }

    public static bool TryTake(out DeferredReply<T> watcher)
    {
        watcher = _pending!;
        _pending = null;
        return watcher != null;
    }

    /// <summary>Reply to <paramref name="context"/> once the task completes (now, if it has).</summary>
    public abstract void Start(CallContext context);

    private protected void Reply(Exception? error)
    {
        var (value, context) = (Value, Context);
        (Value, Context) = (default!, default);
        Return();
        if (error == null) context.ReplyValue(value);
        else context.ReplyException(error);
    }

    private protected abstract void Return();
}

/// <summary><see cref="DeferredReply{T}"/> after a <see cref="ValueTask"/>.</summary>
internal sealed class DeferredAfter<T> : DeferredReply<T>
{
    [ThreadStatic] private static Stack<DeferredAfter<T>>? _pool;
    private readonly Action _completed;
    private ValueTask _task;

    private DeferredAfter() => _completed = Completed;

    public static void Set(ValueTask done, T reply)
    {
        var watcher = _pool is { Count: > 0 } pool ? pool.Pop() : new DeferredAfter<T>();
        watcher._task = done;
        Leave(watcher, reply);
    }

    public override void Start(CallContext context)
    {
        Context = context;
        if (_task.IsCompleted) Completed();
        else _task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_completed);
    }

    private void Completed()
    {
        var task = _task;
        _task = default;
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Reply(e);
            return;
        }

        Reply(null);
    }

    private protected override void Return() => (_pool ??= new Stack<DeferredAfter<T>>()).Push(this);
}

/// <summary><see cref="DeferredReply{T}"/> after a <see cref="ValueTask{TResult}"/> (a call whose result is not the reply).</summary>
internal sealed class DeferredAfter<TDone, T> : DeferredReply<T>
{
    [ThreadStatic] private static Stack<DeferredAfter<TDone, T>>? _pool;
    private readonly Action _completed;
    private ValueTask<TDone> _task;

    private DeferredAfter() => _completed = Completed;

    public static void Set(ValueTask<TDone> done, T reply)
    {
        var watcher = _pool is { Count: > 0 } pool ? pool.Pop() : new DeferredAfter<TDone, T>();
        watcher._task = done;
        Leave(watcher, reply);
    }

    public override void Start(CallContext context)
    {
        Context = context;
        if (_task.IsCompleted) Completed();
        else _task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_completed);
    }

    private void Completed()
    {
        var task = _task;
        _task = default;
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Reply(e);
            return;
        }

        Reply(null);
    }

    private protected override void Return() => (_pool ??= new Stack<DeferredAfter<TDone, T>>()).Push(this);
}
