using System.Threading.Tasks.Sources;

namespace DaServer.Rpc;

/// <summary>A call waiting for its reply.</summary>
internal interface IPendingCall
{
    /// <summary>Thread of the caller that completes it there (a shard), or null: completed inline.</summary>
    CompletionThread? Caller { get; }

    /// <summary>Complete with a reply. Runs on an IO thread; payload is only valid during the call.</summary>
    void Complete(RpcStatus status, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Fail with a status: status-only for the runtime's refusals (timeout, overload, lost connection: nothing
    /// formatted), with a <paramref name="message"/> only for programming errors found on a cold path.
    /// </summary>
    void Fail(RpcStatus status, string? message = null, TimeSpan retryAfter = default);
}


/// <summary>Reply type of calls that return no value (control calls): nothing is deserialized.</summary>
internal readonly struct NoReply;

/// <summary>
/// Pooled, allocation-free awaitable for an in-flight call returning <typeparamref name="T"/> (<see cref="Task"/>), or
/// nothing (<see cref="Done"/>, with <see cref="NoReply"/>).
/// <para>Completion threading: when the caller is a shard thread (<see cref="Caller"/> set at creation) the
/// runtime completes the call <b>on that shard thread</b> (the reply is posted to the shard's mailbox), so the
/// continuation runs inline there: no SynchronizationContext.Post, no thread-pool hop, no allocation. Callers outside
/// a shard (tests, tools, the game client) get their continuation on the thread pool, queued as this object itself
/// (<see cref="IThreadPoolWorkItem"/>), so that path allocates nothing either.</para>
/// ExecutionContext (AsyncLocal) is intentionally not flowed across RPC awaits.
/// </summary>
internal sealed class PendingCall<T> : IPendingCall, IValueTaskSource<T>, IValueTaskSource
#if NET
    , IThreadPoolWorkItem
#endif
{
    /// <summary>Calls made outside shards (clients, tools, tests): their in-flight count is bounded by their table.</summary>
    private static readonly ObjectPool<PendingCall<T>> Pool = new(() => new PendingCall<T>(), 1024);

    private static readonly Action<object?> CompletedSentinel = static _ => { };
    private static readonly Action<object?> ResumeQueued = static call => ((PendingCall<T>)call!).Resume(((PendingCall<T>)call!)._queued!);

    private Action<object?>? _continuation;
    private Action<object?>? _queued; // continuation handed to the thread pool (see Execute)
    private object? _continuationState;
    private SynchronizationContext? _capturedContext;
    private Exception? _error;
    private T _result = default!;
    private short _version;
    private long _chain; // the caller's call chain, restored around its continuation

    private PendingCall()
    {
    }

    public static PendingCall<T> Rent(CompletionThread? caller)
    {
        // A shard's calls come from its own per-thread pool (rented and returned on its thread: as many as its peak in flight).
        var call = caller != null && PerThread<PendingCall<T>>.TryRent(out var local) ? local : Pool.Rent();
        call.Caller = caller;
        call._chain = caller != null ? CallChain.Current : 0;
        return call;
    }

    public CompletionThread? Caller { get; private set; }

    public ValueTask<T> Task => new(this, _version);

    /// <summary>The call as a <see cref="ValueTask"/> (calls without a reply value).</summary>
    public ValueTask Done => new(this, _version);

    public void Complete(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        if (status != RpcStatus.Ok)
        {
            Fail(RpcException.FromPayload(status, payload));
            return;
        }

        if (typeof(T) != typeof(NoReply))
        {
            try
            {
                _result = NinoDeserializer.Deserialize<T>(payload);
            }
            catch (Exception e)
            {
                Fail(e);
                return;
            }
        }

        Signal();
    }

    public void Fail(RpcStatus status, string? message = null, TimeSpan retryAfter = default) =>
        Fail(new RpcException(status, message, retryAfter));

    private void Fail(Exception error)
    {
        _error = error;
        Signal();
    }

    /// <summary>
    /// Publish completion and run the continuation if one is already registered. Completion and the continuation are
    /// one atomic state (<see cref="CompletedSentinel"/>): with a separate flag the awaiter could see it, reset this
    /// pooled object and register the next call's continuation before the exchange, which would then run it early.
    /// </summary>
    private void Signal()
    {
        var continuation = Interlocked.Exchange(ref _continuation, CompletedSentinel);
        if (continuation == null) return;

        var context = _capturedContext;
        if (context != null)
            PostTo(context, continuation, _continuationState); // foreign UI-style contexts only
        else if (Caller is { } caller)
        {
            // Replies are handed to the caller's thread before they complete; a failure (timeout, lost link) completes
            // wherever it is found (the maintenance timer, an IO thread): the caller's code must still resume on its own thread.
            if (ReferenceEquals(CompletionThread.Current, caller)) Resume(continuation);
            else PostToCaller(caller, continuation);
        }
        else
            Queue(continuation);
    }

    private void PostToCaller(CompletionThread caller, Action<object?> continuation)
    {
        _queued = continuation;
        caller.Post(ResumeQueued, this);
    }

    /// <summary>Run the continuation on the caller's thread, in the caller's call chain.</summary>
    private void Resume(Action<object?> continuation)
    {
        _queued = null;
        var previous = CallChain.Current;
        CallChain.Current = _chain;
        try
        {
            continuation(_continuationState); // resets this object: read everything before
        }
        finally
        {
            CallChain.Current = previous;
        }
    }

    private void Queue(Action<object?> continuation)
    {
        _queued = continuation;
#if NET
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
#else
        ThreadPool.UnsafeQueueUserWorkItem(static call => ((PendingCall<T>)call!).Execute(), this);
#endif
    }

#if NET
    void IThreadPoolWorkItem.Execute() => Execute();
#endif

    private void Execute()
    {
        var continuation = _queued!;
        _queued = null;
        continuation(_continuationState); // the continuation resets this object: read everything before
    }

    // Separate method: a lambda capturing parameters allocates its closure on entry to the method that declares it,
    // even on branches that never use it; here that cost stays on this rare path.
    private static void PostTo(SynchronizationContext context, Action<object?> continuation, object? state) =>
        context.Post(static s => ((Action)s!)(), (Action)(() => continuation(state)));

    public ValueTaskSourceStatus GetStatus(short token)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");
        if (!ReferenceEquals(Volatile.Read(ref _continuation), CompletedSentinel)) return ValueTaskSourceStatus.Pending;
        return _error == null ? ValueTaskSourceStatus.Succeeded
            : _error is OperationCanceledException ? ValueTaskSourceStatus.Canceled : ValueTaskSourceStatus.Faulted;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");

        if (Caller == null && (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
        {
            var sc = SynchronizationContext.Current;
            if (sc != null && sc.GetType() != typeof(SynchronizationContext))
                _capturedContext = sc;
        }

        _continuationState = state;
        if (Interlocked.CompareExchange(ref _continuation, continuation, null) != null)
        {
            // Completed between the awaiter's IsCompleted check and now: never run inline here, and resume a shard's
            // code on its shard.
            if (Caller is { } caller && _capturedContext == null) PostToCaller(caller, continuation);
            else Queue(continuation);
        }
    }

    /// <summary>Return the result (or throw the stored error) and reset for reuse.</summary>
    public T GetResult(short token)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");
        if (!ReferenceEquals(Volatile.Read(ref _continuation), CompletedSentinel)) NotCompleted();
        var (result, error, caller) = (_result, _error, Caller);
        _result = default!;
        _error = null;
        _continuation = null;
        _continuationState = null;
        _capturedContext = null;
        Caller = null;
        _version++;
        if (caller != null && ReferenceEquals(CompletionThread.Current, caller))
            PerThread<PendingCall<T>>.Return(this);
        else
            Pool.Return(this);
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);

    /// <summary>
    /// <c>.Result</c> or <c>GetAwaiter().GetResult()</c> on a call still in flight: a pooled source cannot block, and a
    /// shard thread waiting for its own reply would never get it (DAS009 reports such waits in handlers and systems).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NotCompleted() =>
        throw new InvalidOperationException("The call has not completed: await it (a call's result is read once, after it completes)");
}
