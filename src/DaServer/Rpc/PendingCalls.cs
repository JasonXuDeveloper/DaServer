using System.Threading.Tasks.Sources;

namespace DaServer.Rpc;

/// <summary>A call waiting for its response.</summary>
internal interface IPendingCall
{
    /// <summary>Shard of the caller: the response is completed on that shard thread (null: completed inline).</summary>
    Shard? CallerShard { get; }

    /// <summary>Complete with a response. Runs on an IO thread; payload is only valid during the call.</summary>
    void Complete(RpcStatus status, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Fail with a status: status-only for the runtime's refusals (timeout, overload, lost connection: nothing
    /// formatted), with a <paramref name="message"/> only for programming errors found on a cold path.
    /// </summary>
    void Fail(RpcStatus status, string? message = null);
}

/// <summary>
/// Fixed-size table of in-flight calls indexed by request id. Lock-free, allocation-free.
/// Request id = (slot generation &lt;&lt; IndexBits) | slot index, so a late response for a recycled slot
/// never completes the wrong call.
/// </summary>
internal sealed class PendingCallTable
{
    private struct Entry
    {
        public long Tag; // request id while active, 0 when free
        public IPendingCall? Call;
        public object? Owner;
        public long Deadline;
        public uint Generation;
    }

    private readonly Entry[] _entries;
    private readonly MpmcQueue<int> _free;
    private readonly int _indexBits;
    private readonly uint _indexMask;
    private int _highWater;

    /// <param name="indexBits">log2 of the max number of concurrent calls (default 2^16 = 65536).</param>
    public PendingCallTable(int indexBits = 16)
    {
        if (indexBits < 4 || indexBits > 24) throw new ArgumentOutOfRangeException(nameof(indexBits));
        _indexBits = indexBits;
        int size = 1 << indexBits;
        _indexMask = (uint)size - 1;
        _entries = new Entry[size];
        _free = new MpmcQueue<int>(size);
        for (int i = 0; i < size; i++) _free.TryEnqueue(i);
    }


    /// <summary>Register <paramref name="call"/>; false when the table is full (the caller refuses it as <see cref="RpcStatus.Overloaded"/>).</summary>
    /// <param name="owner">Optional owner (e.g. the connection) for <see cref="FailOwnedBy"/>.</param>
    public bool TryRegister(IPendingCall call, int timeoutMs, out uint requestId, object? owner = null)
    {
        if (!_free.TryDequeue(out int index))
        {
            requestId = 0;
            return false;
        }

        ref var e = ref _entries[index];
        uint generation = e.Generation + 1;
        uint id = (generation << _indexBits) | (uint)index;
        if (id == 0)
        {
            generation++;
            id = (generation << _indexBits) | (uint)index;
        }

        e.Generation = generation;
        e.Call = call;
        e.Owner = owner;
        e.Deadline = timeoutMs > 0 ? Stopwatch.GetTimestamp() + timeoutMs * (Stopwatch.Frequency / 1000) : long.MaxValue;
        Volatile.Write(ref e.Tag, id); // publish last

        int hw;
        while (index >= (hw = Volatile.Read(ref _highWater)) &&
               Interlocked.CompareExchange(ref _highWater, index + 1, hw) != hw)
        {
        }

        requestId = id;
        return true;
    }

    /// <summary>Remove and return the call registered under <paramref name="requestId"/>, or null.</summary>
    public IPendingCall? Take(uint requestId)
    {
        int index = (int)(requestId & _indexMask);
        ref var e = ref _entries[index];
        if (Interlocked.CompareExchange(ref e.Tag, 0, requestId) != requestId)
            return null;

        var call = e.Call;
        e.Call = null;
        e.Owner = null;
        _free.TryEnqueue(index);
        return call;
    }

    public bool TryComplete(uint requestId, RpcStatus status, ReadOnlySpan<byte> payload)
    {
        var call = Take(requestId);
        if (call == null)
            return false; // timed out / already completed

        call.Complete(status, payload);
        return true;
    }

    /// <summary>Fail calls whose deadline passed. Call periodically (e.g. every 100 ms).</summary>
    public int SweepExpired()
    {
        long now = Stopwatch.GetTimestamp();
        int failed = 0;
        int hw = Volatile.Read(ref _highWater);
        for (int i = 0; i < hw; i++)
        {
            ref var e = ref _entries[i];
            long tag = Volatile.Read(ref e.Tag);
            if (tag != 0 && e.Deadline < now)
            {
                var call = Take((uint)tag);
                if (call != null)
                {
                    call.Fail(RpcStatus.Timeout);
                    failed++;
                }
            }
        }

        return failed;
    }

    /// <summary>Fail every in-flight call.</summary>
    public void FailAll(RpcStatus status)
    {
        int hw = Volatile.Read(ref _highWater);
        for (int i = 0; i < hw; i++)
        {
            long tag = Volatile.Read(ref _entries[i].Tag);
            if (tag != 0)
                Take((uint)tag)?.Fail(status);
        }
    }

    /// <summary>Fail every call registered with <paramref name="owner"/> (e.g. when its connection drops).</summary>
    public void FailOwnedBy(object owner, RpcStatus status)
    {
        int hw = Volatile.Read(ref _highWater);
        for (int i = 0; i < hw; i++)
        {
            ref var e = ref _entries[i];
            long tag = Volatile.Read(ref e.Tag);
            if (tag != 0 && ReferenceEquals(e.Owner, owner))
                Take((uint)tag)?.Fail(status);
        }
    }
}

/// <summary>Reply type of calls that return no value (control calls): nothing is deserialized.</summary>
internal readonly struct NoReply;

/// <summary>
/// Pooled, allocation-free awaitable for an in-flight call returning <typeparamref name="T"/> (<see cref="Task"/>), or
/// nothing (<see cref="Done"/>, with <see cref="NoReply"/>).
/// <para>Completion threading: when the caller is a shard thread (<see cref="CallerShard"/> set at creation) the
/// runtime completes the call <b>on that shard thread</b> (the response is posted to the shard's mailbox), so the
/// continuation runs inline there: no SynchronizationContext.Post, no thread-pool hop, no allocation. Callers outside
/// a shard (tests, tools, the game client) get their continuation on the thread pool, queued as this object itself
/// (<see cref="IThreadPoolWorkItem"/>), so that path allocates nothing either.</para>
/// ExecutionContext (AsyncLocal) is intentionally not flowed across RPC awaits.
/// </summary>
internal sealed class PendingCall<T> : IPendingCall, IValueTaskSource<T>, IValueTaskSource, IThreadPoolWorkItem
{
    /// <summary>Calls made outside shards (clients, tools, tests): their in-flight count is bounded by their table.</summary>
    private static readonly ObjectPool<PendingCall<T>> Pool = new(() => new PendingCall<T>(), 1024);

    /// <summary>
    /// Calls made by this shard thread. A shard rents and returns on its own thread (its continuations run there), so
    /// the stack needs no synchronization and holds exactly the shard's peak number of calls in flight: a burst of any
    /// width allocates only the first time.
    /// </summary>
    [ThreadStatic] private static Stack<PendingCall<T>>? _shardPool;
    private static readonly Action<object?> CompletedSentinel = static _ => { };

    private Action<object?>? _continuation;
    private Action<object?>? _queued; // continuation handed to the thread pool (see Execute)
    private object? _continuationState;
    private SynchronizationContext? _capturedContext;
    private Exception? _error;
    private T _result = default!;
    private short _version;

    private PendingCall()
    {
    }

    public static PendingCall<T> Rent(Shard? caller)
    {
        var call = caller != null && _shardPool is { Count: > 0 } local ? local.Pop() : Pool.Rent();
        call.CallerShard = caller;
        return call;
    }

    public Shard? CallerShard { get; private set; }

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
                var reader = new Reader(payload);
                NinoDeserializer.Deserialize(out _result, ref reader);
            }
            catch (Exception e)
            {
                Fail(e);
                return;
            }
        }

        Signal();
    }

    public void Fail(RpcStatus status, string? message = null) => Fail(new RpcException(status, message));

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
        else if (CallerShard != null)
            continuation(_continuationState); // completed on the caller's shard thread
        else
            Queue(continuation);
    }

    private void Queue(Action<object?> continuation)
    {
        _queued = continuation;
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    void IThreadPoolWorkItem.Execute()
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

        if (CallerShard == null && (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
        {
            var sc = SynchronizationContext.Current;
            if (sc != null && sc.GetType() != typeof(SynchronizationContext))
                _capturedContext = sc;
        }

        _continuationState = state;
        if (Interlocked.CompareExchange(ref _continuation, continuation, null) != null)
            Queue(continuation); // completed between the awaiter's IsCompleted check and now: never run inline here
    }

    /// <summary>Return the result (or throw the stored error) and reset for reuse.</summary>
    public T GetResult(short token)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");
        var (result, error, shard) = (_result, _error, CallerShard);
        _result = default!;
        _error = null;
        _continuation = null;
        _continuationState = null;
        _capturedContext = null;
        CallerShard = null;
        _version++;
        if (shard != null && ReferenceEquals(Shard.Current, shard))
            (_shardPool ??= new Stack<PendingCall<T>>()).Push(this);
        else
            Pool.Return(this);
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);
}
