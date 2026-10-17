using System.Numerics;

namespace DaServer.Buffers;

/// <summary>
/// Power-of-two bucketed byte[] pool shared by the whole process.
/// On .NET 5+ arrays are allocated on the Pinned Object Heap: sockets can use them without per-operation
/// pinning and they never fragment the compacting heaps. Steady-state rent/return is allocation free.
/// </summary>
public static class BufferPool
{
    public const int MinSize = 256;          // 2^8
    public const int MaxPooledSize = 1 << 22; // 4 MB
    private const int MinShift = 8;
    private const int BucketCount = 22 - MinShift + 1;
    private const int BucketBudget = 64 << 20; // bytes retained per bucket at most

    private static readonly MpmcQueue<byte[]>[] Buckets = CreateBuckets();

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
        if (Buckets[bucket].TryDequeue(out var buffer))
            return buffer;

        return Allocate(MinSize << bucket);
    }

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
        // ceil(log2(length)) - MinShift
#if NET
        int log = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(length - 1));
#else
        int log = 0;
        for (uint v = (uint)(length - 1); v != 0; v >>= 1) log++;
#endif
        return log - MinShift;
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
#if NET
        return GC.AllocateUninitializedArray<byte>(length, pinned: true);
#else
        return new byte[length];
#endif
    }
}
