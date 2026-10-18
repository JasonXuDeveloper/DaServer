namespace DaServer.Rpc;

/// <summary>
/// Table of in-flight calls indexed by call id. Lock-free, allocation-free once warm. Call id = (slot generation
/// &lt;&lt; IndexBits) | slot index, so a late reply for a recycled slot never completes the wrong call. The generation
/// has only 32 - IndexBits bits and the free stacks reuse the slot freed last, so a failed call's slot (timed out,
/// its connection gone: its reply may still come) cools for <see cref="CoolMs"/> before it is reused, unless the table
/// is full: a late reply then finds no call instead of a wrapped generation's. Memory follows
/// use, not the bound: slots live in chunks of doubling size (<see cref="FirstChunk"/>, then twice as many each),
/// allocated as the number of calls in flight first reaches them, so a client with a couple of calls out holds
/// <see cref="FirstChunk"/> slots and a busy table about twice its peak; free slots form a stack threaded through
/// the slots themselves, in stripes: each thread pushes the slots it frees onto its own stripe and pops from it first,
/// so threads do not all swap one head on every call and reply. An entry is 32 bytes (two per cache line): the call id fits an int, and the deadline is the low
/// 32 bits of <see cref="Platform.Milliseconds"/>, compared with wrap-around (deadlines are seconds away, not weeks).
/// </summary>
internal sealed class PendingCallTable
{
    private struct Entry
    {
        public IPendingCall? Call;
        public object? Owner;
        public int Tag; // call id while active, 0 when free, Cooling or Failing for a failed call's slot
        public int Deadline; // low 32 bits of the deadline in Platform.Milliseconds, 0 = none
        public uint Generation;
        public int NextFree; // free stack link: index + 1 of the next free slot, 0 = none; cooling: until when (low 32 bits)
    }

    // Tags no call id takes (TryRegister skips them): a failed call's slot while its time is written, then while it cools.
    private const int Failing = -2, Cooling = -1;

    /// <summary>How long a failed call's slot is not reused: longer than any request's budget (25.5 s, the header's).</summary>
    internal const int CoolMs = 30_000;

    private const int FirstChunkBits = 3;
    internal const int FirstChunk = 1 << FirstChunkBits;

    private readonly Entry[]?[] _chunks;
    private readonly Lock _grow = new();
    private readonly int _indexBits;
    private readonly uint _indexMask;
    private readonly int _size;
    private int _allocated; // slots handed out at least once (the prefix sweeps scan), at most _size
    // Per stripe, (version << 32) | (index + 1) of the top free slot; the version defeats ABA. Calls and replies swap
    // them: each alone on its cache line, off the fields every call reads.
    private readonly PaddedLong[] _freeHeads;
    private readonly int _stripeMask;

    /// <param name="indexBits">log2 of the max number of concurrent calls (default 2^16 = 65536).</param>
    /// <param name="stripes">Free stacks (a power of two): one per thread that calls at once is ideal; a client's table, used by
    /// one or two threads and numerous in a swarm, takes one.</param>
    public PendingCallTable(int indexBits = 16, int stripes = 1)
    {
        if (stripes < 1 || (stripes & (stripes - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(stripes));
        _freeHeads = new PaddedLong[stripes];
        _stripeMask = stripes - 1;
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
            return allocated == 0 ? 0 : Math.Min(StartOf(ChunkOf(allocated - 1) + 1), _size);
        }
    }

    // Chunk k holds FirstChunk << k slots and starts at slot FirstChunk * (2^k - 1); the last one ends at the table's size.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChunkOf(int index) => 31 - Platform.LeadingZeroCount((uint)(index >> FirstChunkBits) + 1);

    private static int StartOf(int chunk) => FirstChunk * ((1 << chunk) - 1);

    // Invariant: a chunk exists for every index below _allocated (it is published before the index is handed out).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Entry At(int index)
    {
        int chunk = ChunkOf(index);
        return ref _chunks[chunk]![index - StartOf(chunk)];
    }

    /// <summary>The deadline of a call made now with <paramref name="timeout"/> (none when it is not positive).</summary>
    public static long DeadlineAfter(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? Platform.Milliseconds + (long)timeout.TotalMilliseconds : 0;

    /// <summary>Register <paramref name="call"/>; false when the table is full (the caller refuses it as <see cref="RpcStatus.Overloaded"/>).</summary>
    /// <param name="deadlineMs">When the call times out, in <see cref="Platform.Milliseconds"/>; 0: never.</param>
    /// <param name="owner">Optional owner (e.g. the connection) for <see cref="FailOwnedBy"/>.</param>
    public bool TryRegister(IPendingCall call, long deadlineMs, out uint callId, object? owner = null)
    {
        if (!TryPopFree(out int index) && !TryAllocate(out index) && !TryWarm(out index))
        {
            callId = 0;
            return false;
        }

        ref var e = ref At(index);
        uint generation = e.Generation + 1;
        uint id = (generation << _indexBits) | (uint)index;
        while (id == 0 || (int)id is Failing or Cooling) // tags that are not call ids
        {
            generation++;
            id = (generation << _indexBits) | (uint)index;
        }

        e.Generation = generation;
        e.Call = call;
        e.Owner = owner;
        e.Deadline = deadlineMs == 0 ? 0 : (int)deadlineMs | ((int)deadlineMs == 0 ? 1 : 0); // a deadline never reads as none
        Volatile.Write(ref e.Tag, (int)id); // publish last
        callId = id;
        return true;
    }

    /// <summary>A free slot: from this thread's stripe first, then from the others in turn.</summary>
    private bool TryPopFree(out int index)
    {
        int stripe = Environment.CurrentManagedThreadId;
        for (int i = 0; i <= _stripeMask; i++)
            if (TryPop(ref _freeHeads[(stripe + i) & _stripeMask].Value, out index)) return true;
        index = -1;
        return false;
    }

    private bool TryPop(ref long freeHead, out int index)
    {
        while (true)
        {
            long head = Volatile.Read(ref freeHead);
            int top = (int)head - 1;
            if (top < 0)
            {
                index = -1;
                return false;
            }

            // A stale read of the link is harmless: the slot was popped meanwhile, which changed the version.
            int next = Volatile.Read(ref At(top).NextFree);
            if (Interlocked.CompareExchange(ref freeHead, (head >> 32) + 1 << 32 | (uint)next, head) == head)
            {
                index = top;
                return true;
            }
        }
    }

    /// <summary>Free a slot onto this thread's stripe.</summary>
    private void PushFree(int index)
    {
        ref var e = ref At(index);
        ref long freeHead = ref _freeHeads[Environment.CurrentManagedThreadId & _stripeMask].Value;
        while (true)
        {
            long head = Volatile.Read(ref freeHead);
            Volatile.Write(ref e.NextFree, (int)head);
            if (Interlocked.CompareExchange(ref freeHead, (head >> 32) + 1 << 32 | (uint)(index + 1), head) == head)
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

    /// <summary>A full table takes a cooling slot back early (cold: a scan).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryWarm(out int index)
    {
        int allocated = Volatile.Read(ref _allocated);
        for (index = 0; index < allocated; index++)
            if (Interlocked.CompareExchange(ref At(index).Tag, 0, Cooling) == Cooling) return true;
        index = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AllocateChunk(int chunk)
    {
        lock (_grow)
            if (_chunks[chunk] == null)
                Volatile.Write(ref _chunks[chunk], new Entry[Math.Min(FirstChunk << chunk, _size - StartOf(chunk))]);
    }

    /// <summary>Remove and return the call registered under <paramref name="callId"/>, or null.</summary>
    public IPendingCall? Take(uint callId)
    {
        int index = (int)(callId & _indexMask);
        if (index >= Volatile.Read(ref _allocated))
            return null; // never issued (a forged or corrupt id)

        ref var e = ref At(index);
        if (Interlocked.CompareExchange(ref e.Tag, 0, (int)callId) != (int)callId)
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
    public int SweepExpired() => Fail(RpcStatus.Timeout, owner: null, expired: true);

    /// <summary>Fail every in-flight call.</summary>
    public void FailAll(RpcStatus status) => Fail(status, owner: null, expired: false);

    /// <summary>Whether a call registered with <paramref name="owner"/> is still in flight (a scan: cold paths).</summary>
    public bool AnyOwnedBy(object owner)
    {
        int allocated = Volatile.Read(ref _allocated);
        for (int i = 0; i < allocated; i++)
        {
            ref var e = ref At(i);
            if (Volatile.Read(ref e.Tag) != 0 && ReferenceEquals(e.Owner, owner)) return true;
        }

        return false;
    }

    /// <summary>Fail every call registered with <paramref name="owner"/> (e.g. when its connection drops).</summary>
    public void FailOwnedBy(object owner, RpcStatus status) => Fail(status, owner, expired: false);

    /// <summary>The one scan that fails calls: those of <paramref name="owner"/> (null: any), whose deadline passed if <paramref name="expired"/>.</summary>
    private int Fail(RpcStatus status, object? owner, bool expired)
    {
        int now = (int)Platform.Milliseconds;
        int failed = 0;
        int allocated = Volatile.Read(ref _allocated);
        for (int i = 0; i < allocated; i++)
        {
            ref var e = ref At(i);
            int tag = Volatile.Read(ref e.Tag);
            if (tag == Cooling)
            {
                if (expired && now - Volatile.Read(ref e.NextFree) > 0 && Interlocked.CompareExchange(ref e.Tag, 0, Cooling) == Cooling)
                    PushFree(i); // cooled down: the sweep frees it
                continue;
            }

            if (tag is 0 or Failing || (owner != null && !ReferenceEquals(e.Owner, owner)) || (expired && (e.Deadline == 0 || now - e.Deadline <= 0)))
                continue;
            if (Interlocked.CompareExchange(ref e.Tag, Failing, tag) != tag) continue; // answered meanwhile
            var call = e.Call!;
            e.Call = null;
            e.Owner = null;
            Volatile.Write(ref e.NextFree, now + CoolMs);
            Volatile.Write(ref e.Tag, Cooling);
            call.Fail(status);
            failed++;
        }

        return failed;
    }
}
