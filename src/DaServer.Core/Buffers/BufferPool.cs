using System.Numerics;

namespace DaServer.Buffers;

/// <summary>
/// Power-of-two bucketed byte[] pool shared by the whole process.
/// On .NET 5+ arrays are allocated on the Pinned Object Heap: sockets can use them without per-operation
/// pinning and they never fragment the compacting heaps. Steady-state rent/return is allocation free. What a peak left
/// behind goes back slowly: every <see cref="TrimEvery"/>, half of the buffers a bucket held through the whole period
/// without lending them are released. A bucket is a FIFO queue, so those are exactly the ones queued before the last
/// trim that the renters' cursor has not reached since; buffers in use, even in rare bursts, are never trimmed, and
/// renting pays nothing for it.
/// </summary>
public static class BufferPool
{
    public const int MinSize = 256;          // 2^8
    public const int MaxPooledSize = 1 << 22; // 4 MB
    private const int MinShift = 8;
    private const int BucketCount = 22 - MinShift + 1;
    private const int BucketBudget = 64 << 20; // bytes retained per bucket at most

    private static readonly MpmcQueue<byte[]>[] Buckets = CreateBuckets();

    /// <summary>How often buckets give back half of what they did not use since the last trim.</summary>
    internal static readonly TimeSpan TrimEvery = TimeSpan.FromSeconds(60);

    // Per bucket: how many buffers had ever been returned to it at the last trim. Those the renters have not reached
    // since (it is first in, first out) stayed unused the whole period. Only the trimmer reads and writes it.
    private static readonly long[] EnqueuedAtTrim = new long[BucketCount];
    private static Timer? _trimmer;

    private static MpmcQueue<byte[]>[] CreateBuckets()
    {
        // Each bucket retains up to BucketBudget bytes (16..65536 buffers). The pool must hold the peak number of
        // buffers in flight, or every burst above it allocates again: a broadcast to N sessions has N send batches
        // (4-16 KB) in flight at once, so small buckets need tens of thousands of slots (4 KB: 16384). Memory is only
        // retained up to what the process actually rented at its peak; the slot arrays cost about 4 MB in total.
        var buckets = new MpmcQueue<byte[]>[BucketCount];
        for (int i = 0; i < BucketCount; i++)
        {
            int capacity = Math.Clamp(BucketBudget >> (MinShift + i), 16, 65536);
            buckets[i] = new MpmcQueue<byte[]>(capacity);
        }

        return buckets;
    }

    /// <summary>The pool as an <see cref="System.Buffers.ArrayPool{T}"/>, for writers that rent through one (Nino's <c>NinoArrayBufferWriter</c>).</summary>
    public static System.Buffers.ArrayPool<byte> ArrayPool { get; } = new Pinned();

    private sealed class Pinned : System.Buffers.ArrayPool<byte>
    {
        public override byte[] Rent(int minimumLength) => BufferPool.Rent(minimumLength);

        public override void Return(byte[] array, bool clearArray = false) => BufferPool.Return(array);
    }

    /// <summary>Rent a buffer of at least <paramref name="minimumLength"/> bytes (contents are undefined).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] Rent(int minimumLength)
    {
        if (minimumLength > MaxPooledSize)
            return Allocate(minimumLength);

        int bucket = BucketIndex(minimumLength);
        var queue = Buckets[bucket];
        if (queue.TryDequeue(out var buffer))
            return buffer;

        return Allocate(MinSize << bucket);
    }

    /// <summary>
    /// Release half of each bucket's buffers that stayed unused since the last trim, then start a new period (the trimmer
    /// timer calls it every <see cref="TrimEvery"/>; one caller at a time). The next buffers out of a bucket are its
    /// oldest, the unused ones: they are what goes.
    /// </summary>
    internal static void Trim()
    {
        lock (Buckets)
            for (int bucket = 0; bucket < BucketCount; bucket++)
            {
                var queue = Buckets[bucket];
                long unused = EnqueuedAtTrim[bucket] - queue.Dequeued;
                for (long release = unused / 2; release > 0 && queue.TryDequeue(out _); release--)
                {
                }

                EnqueuedAtTrim[bucket] = queue.Enqueued;
            }
    }

    /// <summary>The trimmer starts with the pool's first growth: a process that never grows it runs no timer.</summary>
    private static void StartTrimmer()
    {
        var timer = new Timer(static _ => Trim(), null, Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _trimmer, timer, null) == null) timer.Change(TrimEvery, TrimEvery);
        else timer.Dispose();
    }

    /// <summary>Buffers the bucket for <paramref name="size"/> holds now (tests).</summary>
    internal static int Retained(int size) => Buckets[BucketIndex(size)].Count;

    /// <summary>Return a buffer obtained from <see cref="Rent"/>. Buffers of foreign sizes are ignored.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(byte[]? buffer)
    {
        if (buffer == null || buffer.Length < MinSize || buffer.Length > MaxPooledSize)
            return;

        int bucket = BucketIndex(buffer.Length);
        if ((MinSize << bucket) != buffer.Length)
            return; // not one of ours

        Buckets[bucket].TryEnqueue(buffer);
    }

    /// <summary>Replace <paramref name="buffer"/> with a rented one of at least <paramref name="size"/> bytes holding its first <paramref name="used"/> bytes; the old one goes back.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Grow(ref byte[] buffer, int used, int size)
    {
        var grown = Rent(size);
        System.Buffer.BlockCopy(buffer, 0, grown, 0, used);
        Return(buffer);
        buffer = grown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketIndex(int length)
    {
        if (length <= MinSize) return 0;
        return 32 - Platform.LeadingZeroCount((uint)(length - 1)) - MinShift; // ceil(log2(length)) - MinShift
    }


    /// <summary>
    /// Bytes allocated because a bucket was empty (pool growth, kept for reuse once returned), and for oversized
    /// rentals. Tests tell growth to a new peak apart from garbage with it.
    /// </summary>
    internal static long AllocatedBytes => Volatile.Read(ref _allocatedBytes);

    private static long _allocatedBytes;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] Allocate(int length)
    {
        Interlocked.Add(ref _allocatedBytes, length);
        if (_trimmer == null) StartTrimmer();
#if NET
        return GC.AllocateUninitializedArray<byte>(length, pinned: true);
#else
        return new byte[length];
#endif
    }
}
