using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using DaServer.Concurrency;
using DaServer.Protocol;
using Nino.Core;

namespace DaServer.Rpc;

/// <summary>A call waiting for its response.</summary>
public interface IPendingCall
{
    /// <summary>Complete with a response. Runs on an IO thread; payload is only valid during the call.</summary>
    void Complete(RpcStatus status, ReadOnlySpan<byte> payload);

    void Fail(RpcException error);
}

/// <summary>
/// Fixed-size table of in-flight calls indexed by request id. Lock-free, allocation-free.
/// Request id = (slot generation &lt;&lt; IndexBits) | slot index, so a late response for a recycled slot
/// never completes the wrong call.
/// </summary>
public sealed class PendingCallTable
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

    public int Capacity => _entries.Length;

    /// <summary>Approximate number of in-flight calls.</summary>
    public int Count => _entries.Length - _free.Count;

    /// <param name="owner">Optional owner (e.g. the connection) for <see cref="FailOwnedBy"/>.</param>
    public uint Register(IPendingCall call, int timeoutMs, object? owner = null)
    {
        if (!_free.TryDequeue(out int index))
            throw new RpcException(RpcStatus.Error, "Too many in-flight calls");

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

        return id;
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
                    call.Fail(new RpcException(RpcStatus.Timeout));
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
                Take((uint)tag)?.Fail(new RpcException(status));
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
                Take((uint)tag)?.Fail(new RpcException(status));
        }
    }
}

/// <summary>
/// Pooled, allocation-free awaitable for an in-flight call.
/// <para>Completion threading: when the caller is a shard thread (<see cref="CallerShard"/> set at creation) the
/// runtime completes the call <b>on that shard thread</b> (the response is posted to the shard's mailbox), so the
/// continuation runs inline there: no SynchronizationContext.Post, no thread-pool hop, no allocation. Callers outside
/// a shard (tests, tools, the game client) get their continuation on the thread pool.</para>
/// ExecutionContext (AsyncLocal) is intentionally not flowed across RPC awaits.
/// </summary>
public abstract class PendingCallBase : IPendingCall
{
    private static readonly Action<object?> CompletedSentinel = static _ => { };

    private Action<object?>? _continuation;
    private object? _continuationState;
    private SynchronizationContext? _capturedContext;
    private Exception? _error;
    private short _version;
    private int _completed;

    /// <summary>Shard of the caller (null when the call was made outside any shard thread).</summary>
    public object? CallerShard { get; protected set; }

    protected short Version => _version;

    public abstract void Complete(RpcStatus status, ReadOnlySpan<byte> payload);

    public void Fail(RpcException error) => SetException(error);

    protected void SetException(Exception error)
    {
        _error = error;
        Signal();
    }

    /// <summary>Publish completion and run the continuation if one is already registered.</summary>
    protected void Signal()
    {
        Volatile.Write(ref _completed, 1);
        var continuation = Interlocked.Exchange(ref _continuation, CompletedSentinel);
        if (continuation != null)
            Invoke(continuation, _continuationState);
    }

    private void Invoke(Action<object?> continuation, object? state)
    {
        var context = _capturedContext;
        if (context != null)
            PostTo(context, continuation, state); // foreign UI-style contexts only
        else if (CallerShard != null)
            continuation(state); // completed on the caller's shard thread
        else
            ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: false);
    }

    // Separate method: a lambda capturing parameters allocates its closure on entry to the method that declares it,
    // even on branches that never use it; here that cost stays on this rare path.
    private static void PostTo(SynchronizationContext context, Action<object?> continuation, object? state) =>
        context.Post(static s => ((Action)s!)(), (Action)(() => continuation(state)));

    protected ValueTaskSourceStatus Status(short token)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");
        if (Volatile.Read(ref _completed) == 0) return ValueTaskSourceStatus.Pending;
        return _error == null ? ValueTaskSourceStatus.Succeeded
            : _error is OperationCanceledException ? ValueTaskSourceStatus.Canceled : ValueTaskSourceStatus.Faulted;
    }

    protected void OnCompletedCore(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");

        if (CallerShard == null && (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
        {
            var sc = SynchronizationContext.Current;
            if (sc != null && sc.GetType() != typeof(SynchronizationContext))
                _capturedContext = sc;
        }

        _continuationState = state;
        var previous = Interlocked.CompareExchange(ref _continuation, continuation, null);
        if (previous != null)
        {
            // Completed between the awaiter's IsCompleted check and now: never run inline from OnCompleted.
            ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: false);
        }
    }

    /// <summary>Throw the stored error (if any) and reset for reuse.</summary>
    protected void FinishAndReset(short token)
    {
        if (token != _version) throw new InvalidOperationException("Stale ValueTask token");
        var error = _error;
        _error = null;
        _continuation = null;
        _continuationState = null;
        _capturedContext = null;
        _completed = 0;
        CallerShard = null;
        _version++;
        Return();
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }

    protected abstract void Return();
}

/// <summary>Pooled awaitable for a call returning <typeparamref name="T"/>.</summary>
public sealed class PendingCall<T> : PendingCallBase, IValueTaskSource<T>
{
    private static readonly ObjectPool<PendingCall<T>> Pool = new(() => new PendingCall<T>(), 1024);

    private T _result = default!;

    private PendingCall()
    {
    }

    public static PendingCall<T> Rent(object? callerWorld)
    {
        var call = Pool.Rent();
        call.CallerShard = callerWorld;
        return call;
    }

    public ValueTask<T> Task => new(this, Version);

    public override void Complete(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        if (status != RpcStatus.Ok)
        {
            SetException(RpcErrors.FromPayload(status, payload));
            return;
        }

        try
        {
            var reader = new Reader(payload);
            NinoDeserializer.Deserialize(out _result, ref reader);
        }
        catch (Exception e)
        {
            SetException(e);
            return;
        }

        Signal();
    }

    T IValueTaskSource<T>.GetResult(short token)
    {
        var result = _result;
        _result = default!;
        FinishAndReset(token);
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token) => Status(token);

    void IValueTaskSource<T>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => OnCompletedCore(continuation, state, token, flags);

    protected override void Return() => Pool.Return(this);
}

/// <summary>Pooled awaitable for a call returning no value.</summary>
public sealed class PendingCall : PendingCallBase, IValueTaskSource
{
    private static readonly ObjectPool<PendingCall> Pool = new(() => new PendingCall(), 1024);

    private PendingCall()
    {
    }

    public static PendingCall Rent(object? callerWorld)
    {
        var call = Pool.Rent();
        call.CallerShard = callerWorld;
        return call;
    }

    public ValueTask Task => new(this, Version);

    public override void Complete(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        if (status != RpcStatus.Ok)
            SetException(RpcErrors.FromPayload(status, payload));
        else
            Signal();
    }

    void IValueTaskSource.GetResult(short token) => FinishAndReset(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => Status(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => OnCompletedCore(continuation, state, token, flags);

    protected override void Return() => Pool.Return(this);
}

/// <summary>Encoding of error payloads (a Nino string message).</summary>
public static class RpcErrors
{
    public static RpcException FromPayload(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        string? message = null;
        if (!payload.IsEmpty)
        {
            try
            {
                var reader = new Reader(payload);
                NinoDeserializer.Deserialize(out message, ref reader);
            }
            catch
            {
                // malformed error payload: keep status only
            }
        }

        return new RpcException(status, message);
    }
}
