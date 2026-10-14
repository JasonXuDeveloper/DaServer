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

    private static readonly MpmcQueue<byte[]>[] Buckets = CreateBuckets();

    private static MpmcQueue<byte[]>[] CreateBuckets()
    {
        var buckets = new MpmcQueue<byte[]>[BucketCount];
        for (int i = 0; i < BucketCount; i++)
        {
            // Keep fewer large buffers around: 4096 x 256B ... 16 x 4MB
            int capacity = Math.Max(16, 4096 >> Math.Max(0, i - 4));
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
