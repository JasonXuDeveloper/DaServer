using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using DaServer.Concurrency;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BucketIndex(int length)
    {
        if (length <= MinSize) return 0;
        // ceil(log2(length)) - MinShift
        int log = 32 - BitOperations.LeadingZeroCount((uint)(length - 1));
        return log - MinShift;
    }


    private static byte[] Allocate(int length)
    {
        return GC.AllocateUninitializedArray<byte>(length, pinned: true);
    }
}
