namespace DaServer.Net;

/// <summary>
/// The bounded send side of a frame channel (a TCP <see cref="Connection"/>, a <see cref="WebSocketChannel"/>): frames
/// are copied into a pooled batch buffer under a short lock, one writer takes whole batches (producers keep filling
/// the spare buffer meanwhile), and a peer that falls more than the bound behind is refused. Nothing is allocated per
/// frame: buffers come from <see cref="BufferPool"/>.
/// </summary>
internal sealed class SendQueue(int maxPendingBytes, int bufferSize, bool releaseIdle, bool keepSpare)
{
    private readonly Lock _lock = new();
    private byte[] _pending = [], _spare = [];
    private int _length, _closed, _overflowed;

    /// <summary>The writer's wake-up (its send loop waits on it).</summary>
    public readonly AsyncSignal Signal = new();

    public enum Result
    {
        /// <summary>Queued behind bytes already waiting: the writer is awake.</summary>
        Queued,

        /// <summary>Queued into an empty queue: wake the writer.</summary>
        Wake,

        /// <summary>Refused: the bound is exceeded for the first time; close the channel (off the caller's thread).</summary>
        Overflow,

        /// <summary>Refused: closed, or already overflowed.</summary>
        Closed,
    }

    public bool HasQueued => Volatile.Read(ref _length) > 0;

    /// <summary>Copy <paramref name="frame"/> into the queue (thread-safe, never blocks on IO).</summary>
    public Result Append(ReadOnlySpan<byte> frame)
    {
        lock (_lock)
        {
            if (_closed != 0) return Result.Closed;
            int required = _length + frame.Length;
            if (required > maxPendingBytes)
                return Interlocked.Exchange(ref _overflowed, 1) == 0 ? Result.Overflow : Result.Closed;
            if (required > _pending.Length)
                BufferPool.Grow(ref _pending, _length, Math.Max(bufferSize, Math.Max(_pending.Length * 2, required)));
            frame.CopyTo(new Span<byte>(_pending, _length, frame.Length));
            bool wake = _length == 0;
            Volatile.Write(ref _length, required);
            return wake ? Result.Wake : Result.Queued;
        }
    }

    /// <summary>Take the queued bytes as a batch, the spare buffer taking their place; false when nothing is queued.</summary>
    public bool TryTake(out byte[] batch, out int length)
    {
        lock (_lock)
        {
            batch = _pending;
            length = _length;
            if (length == 0 || _closed != 0) return false;
            (_pending, _spare) = (_spare, []);
            Volatile.Write(ref _length, 0);
            return true;
        }
    }

    /// <summary>
    /// A written batch becomes the spare while more is queued (or always, <c>keepSpare</c>: a channel written on every
    /// beat keeps both buffers); once idle it goes back to the pool, and with <c>releaseIdle</c> the empty queue's
    /// buffer too, so idle channels hold no send memory.
    /// </summary>
    public void Release(byte[] batch)
    {
        lock (_lock)
        {
            bool idle = _length == 0;
            if ((!idle || keepSpare && !releaseIdle) && _spare.Length == 0 && batch.Length >= bufferSize && _closed == 0)
            {
                _spare = batch;
                return;
            }

            if (idle && releaseIdle && _pending.Length > 0)
            {
                BufferPool.Return(_pending);
                _pending = [];
            }
        }

        BufferPool.Return(batch);
    }

    /// <summary>Wake the writer, once at the end of the thread's send batch if one is open (<see cref="BeginBatch"/>).</summary>
    public void Wake()
    {
        if (t_batchDepth > 0) (t_batched ??= new()).Add(this);
        else Signal.Set();
    }

    /// <summary>
    /// Close the queue: nothing more is accepted; the bytes never written still reach <paramref name="tap"/>, then every
    /// buffer goes back to the pool.
    /// </summary>
    public void Close(ISendTap? tap = null)
    {
        byte[] pending, spare;
        int length;
        lock (_lock)
        {
            _closed = 1;
            (pending, spare, length) = (_pending, _spare, _length);
            (_pending, _spare) = ([], []);
            Volatile.Write(ref _length, 0);
        }

        if (length > 0) tap?.OnSending(new ReadOnlySpan<byte>(pending, 0, length)); // outside the lock: a tap may send elsewhere
        BufferPool.Return(pending);
        BufferPool.Return(spare);
    }

    [ThreadStatic] private static int t_batchDepth;
    [ThreadStatic] private static List<SendQueue>? t_batched;

    /// <summary>
    /// Until the returned scope is disposed, sends from this thread only queue bytes; each channel's writer is woken once
    /// at the end. A batch of work that sends many messages (the frames of one receive, one shard wake) then costs one
    /// write per channel instead of one per message (a session in 9 groups gets 9 frames per update).
    /// </summary>
    public static Batch BeginBatch()
    {
        t_batchDepth++;
        return default;
    }

    public readonly struct Batch : IDisposable
    {
        public void Dispose()
        {
            if (--t_batchDepth > 0 || t_batched is not { Count: > 0 } batched) return;
            foreach (var queue in batched) queue.Signal.Set();
            batched.Clear();
        }
    }
}
