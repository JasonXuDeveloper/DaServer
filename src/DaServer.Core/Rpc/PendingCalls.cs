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

/// <summary>
/// Table of in-flight calls indexed by call id. Lock-free, allocation-free once warm. Call id = (slot generation
/// &lt;&lt; IndexBits) | slot index, so a late reply for a recycled slot never completes the wrong call. Memory follows
/// use, not the bound: slots live in chunks of doubling size (<see cref="FirstChunk"/>, then twice as many each),
/// allocated as the number of calls in flight first reaches them, so a client with a couple of calls out holds
/// <see cref="FirstChunk"/> slots and a busy table about twice its peak; free slots form a stack threaded through
/// the slots themselves.
/// </summary>
internal sealed class PendingCallTable
{
    private struct Entry
    {
        public long Tag; // call id while active, 0 when free
        public IPendingCall? Call;
        public object? Owner;
        public long Deadline;
        public uint Generation;
        public int NextFree; // free stack link: index + 1 of the next free slot, 0 = none
    }

    private const int FirstChunkBits = 3;
    internal const int FirstChunk = 1 << FirstChunkBits;

    private readonly Entry[]?[] _chunks;
    private readonly Lock _grow = new();
    private readonly int _indexBits;
    private readonly uint _indexMask;
    private readonly int _size;
    private int _allocated; // slots handed out at least once (the prefix sweeps scan), at most _size
    private long _freeHead; // (version << 32) | (index + 1) of the top free slot; the version defeats ABA

    /// <param name="indexBits">log2 of the max number of concurrent calls (default 2^16 = 65536).</param>
    public PendingCallTable(int indexBits = 16)
    {
        if (indexBits < 4 || indexBits > 24) throw new ArgumentOutOfRangeException(nameof(indexBits));
        _indexBits = indexBits;
        _size = 1 << indexBits;
        _indexMask = (uint)_size - 1;
        _chunks = new Entry[]?[ChunkOf(_size - 1) + 1];
    }

    /// <summary>Slots allocated so far (memory follows the peak number of calls in flight).</summary>
    internal int Capacity
    {
        get
        {
            int allocated = Volatile.Read(ref _allocated);
            return allocated == 0 ? 0 : StartOf(ChunkOf(allocated - 1) + 1);
        }
    }

    // Chunk k holds FirstChunk << k slots and starts at slot FirstChunk * (2^k - 1).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChunkOf(int index) => 31 - LeadingZeros((uint)(index >> FirstChunkBits) + 1);

    private static int StartOf(int chunk) => FirstChunk * ((1 << chunk) - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LeadingZeros(uint value)
    {
#if NET
        return System.Numerics.BitOperations.LeadingZeroCount(value);
#else
        int n = 32;
        while (value != 0)
        {
            value >>= 1;
            n--;
        }

        return n;
#endif
    }

    // Invariant: a chunk exists for every index below _allocated (it is published before the index is handed out).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Entry At(int index)
    {
        int chunk = ChunkOf(index);
        return ref _chunks[chunk]![index - StartOf(chunk)];
    }

    /// <summary>Register <paramref name="call"/>; false when the table is full (the caller refuses it as <see cref="RpcStatus.Overloaded"/>).</summary>
    /// <param name="owner">Optional owner (e.g. the connection) for <see cref="FailOwnedBy"/>.</param>
    public bool TryRegister(IPendingCall call, int timeoutMs, out uint callId, object? owner = null)
    {
        if (!TryPopFree(out int index) && !TryAllocate(out index))
        {
            callId = 0;
            return false;
        }

        ref var e = ref At(index);
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
        callId = id;
        return true;
    }

    private bool TryPopFree(out int index)
    {
        while (true)
        {
            long head = Volatile.Read(ref _freeHead);
            int top = (int)head - 1;
            if (top < 0)
            {
                index = -1;
                return false;
            }

            // A stale read of the link is harmless: the slot was popped meanwhile, which changed the version.
            int next = Volatile.Read(ref At(top).NextFree);
            if (Interlocked.CompareExchange(ref _freeHead, (head >> 32) + 1 << 32 | (uint)next, head) == head)
            {
                index = top;
                return true;
            }
        }
    }

    private void PushFree(int index)
    {
        ref var e = ref At(index);
        while (true)
        {
            long head = Volatile.Read(ref _freeHead);
            Volatile.Write(ref e.NextFree, (int)head);
            if (Interlocked.CompareExchange(ref _freeHead, (head >> 32) + 1 << 32 | (uint)(index + 1), head) == head)
                return;
        }
    }

    /// <summary>A never used slot, allocating its chunk when it is the chunk's first (cold: once per chunk).</summary>
    private bool TryAllocate(out int index)
    {
        while (true)
        {
            int allocated = Volatile.Read(ref _allocated);
            if (allocated == _size)
            {
                index = -1;
                return false;
            }

            int chunk = ChunkOf(allocated);
            if (Volatile.Read(ref _chunks[chunk]) == null) AllocateChunk(chunk);
            if (Interlocked.CompareExchange(ref _allocated, allocated + 1, allocated) == allocated)
            {
                index = allocated;
                return true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AllocateChunk(int chunk)
    {
        lock (_grow)
            if (_chunks[chunk] == null)
                Volatile.Write(ref _chunks[chunk], new Entry[FirstChunk << chunk]);
    }

    /// <summary>Remove and return the call registered under <paramref name="callId"/>, or null.</summary>
    public IPendingCall? Take(uint callId)
    {
        int index = (int)(callId & _indexMask);
        if (index >= Volatile.Read(ref _allocated))
            return null; // never issued (a forged or corrupt id)

        ref var e = ref At(index);
        if (Interlocked.CompareExchange(ref e.Tag, 0, callId) != callId)
            return null;

        var call = e.Call;
        e.Call = null;
        e.Owner = null;
        PushFree(index);
        return call;
    }

    public bool TryComplete(uint callId, RpcStatus status, ReadOnlySpan<byte> payload)
    {
        var call = Take(callId);
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
        int allocated = Volatile.Read(ref _allocated);
        for (int i = 0; i < allocated; i++)
        {
            ref var e = ref At(i);
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
        int allocated = Volatile.Read(ref _allocated);
        for (int i = 0; i < allocated; i++)
        {
            long tag = Volatile.Read(ref At(i).Tag);
            if (tag != 0)
                Take((uint)tag)?.Fail(status);
        }
    }

    /// <summary>Fail every call registered with <paramref name="owner"/> (e.g. when its connection drops).</summary>
    public void FailOwnedBy(object owner, RpcStatus status)
    {
        int allocated = Volatile.Read(ref _allocated);
        for (int i = 0; i < allocated; i++)
        {
            ref var e = ref At(i);
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

    public static PendingCall<T> Rent(CompletionThread? caller)
    {
        var call = caller != null && _shardPool is { Count: > 0 } local ? local.Pop() : Pool.Rent();
        call.Caller = caller;
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
        else if (Caller != null)
            continuation(_continuationState); // completed on the caller's shard thread
        else
            Queue(continuation);
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
            Queue(continuation); // completed between the awaiter's IsCompleted check and now: never run inline here
    }

    /// <summary>Return the result (or throw the stored error) and reset for reuse.</summary>
    public T GetResult(short token)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");
        var (result, error, caller) = (_result, _error, Caller);
        _result = default!;
        _error = null;
        _continuation = null;
        _continuationState = null;
        _capturedContext = null;
        Caller = null;
        _version++;
        if (caller != null && ReferenceEquals(CompletionThread.Current, caller))
            (_shardPool ??= new Stack<PendingCall<T>>()).Push(this);
        else
            Pool.Return(this);
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);
}
