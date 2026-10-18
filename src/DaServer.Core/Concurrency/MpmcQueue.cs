using System.Runtime.InteropServices;

namespace DaServer.Concurrency;

/// <summary>
/// Bounded lock-free multi-producer multi-consumer queue (Vyukov). Slots are structs in one preallocated array,
/// so enqueue/dequeue never allocate. Safe for any number of producers and consumers.
/// </summary>
internal sealed class MpmcQueue<T>
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public long Sequence;
        public T Item;
    }

    private readonly Slot[] _slots;
    private readonly long _mask;

    // Producer and consumer cursors on separate cache lines to avoid false sharing.
    private PaddedLong _enqueuePos;
    private PaddedLong _dequeuePos;

    /// <param name="capacity">Rounded up to a power of two.</param>
    public MpmcQueue(int capacity)
    {
        if (capacity < 2) capacity = 2;
        int size = 1;
        while (size < capacity) size <<= 1;

        _slots = new Slot[size];
        for (int i = 0; i < size; i++) _slots[i].Sequence = i;
        _mask = size - 1;
    }

    public int Capacity => _slots.Length;

    /// <summary>How many items were ever enqueued (the next enqueue's position).</summary>
    internal long Enqueued => Volatile.Read(ref _enqueuePos.Value);

    /// <summary>How many items were ever dequeued (the next dequeue's position): items leave in the order they came.</summary>
    internal long Dequeued => Volatile.Read(ref _dequeuePos.Value);

    /// <summary>Approximate number of queued items.</summary>
    public int Count
    {
        get
        {
            long count = Volatile.Read(ref _enqueuePos.Value) - Volatile.Read(ref _dequeuePos.Value);
            return count < 0 ? 0 : (int)Math.Min(count, _slots.Length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        var slots = _slots;
        long pos = Volatile.Read(ref _enqueuePos.Value);
        while (true)
        {
            ref var slot = ref slots[pos & _mask];
            long seq = Volatile.Read(ref slot.Sequence);
            long diff = seq - pos;
            if (diff == 0)
            {
                if (Interlocked.CompareExchange(ref _enqueuePos.Value, pos + 1, pos) == pos)
                {
                    slot.Item = item;
                    Volatile.Write(ref slot.Sequence, pos + 1);
                    return true;
                }
            }
            else if (diff < 0)
            {
                return false; // full
            }

            pos = Volatile.Read(ref _enqueuePos.Value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        var slots = _slots;
        long pos = Volatile.Read(ref _dequeuePos.Value);
        while (true)
        {
            ref var slot = ref slots[pos & _mask];
            long seq = Volatile.Read(ref slot.Sequence);
            long diff = seq - (pos + 1);
            if (diff == 0)
            {
                if (Interlocked.CompareExchange(ref _dequeuePos.Value, pos + 1, pos) == pos)
                {
                    item = slot.Item;
                    // Release references held by the slot so pooled objects/strings can be collected.
                    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                        slot.Item = default!;
                    Volatile.Write(ref slot.Sequence, pos + _mask + 1);
                    return true;
                }
            }
            else if (diff < 0)
            {
                item = default!;
                return false; // empty
            }

            pos = Volatile.Read(ref _dequeuePos.Value);
        }
    }

}

/// <summary>
/// A long alone on its 64-byte cache line: 64 bytes of padding on each side, because object fields are not
/// 64-byte aligned and adjacent-line prefetch pairs lines (must not be nested in a generic type: explicit layout).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedLong
{
    [FieldOffset(64)] public long Value;
}
