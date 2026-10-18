namespace DaServer.Concurrency;

/// <summary>Called by <see cref="TimerWheel{T}.Advance{THandler}"/> for each timer that is due.</summary>
public interface ITimerHandler<T>
{
    void OnTimer(in T item);
}

/// <summary>
/// Timers as data: items due after a delay, fired by whoever owns the wheel when it advances (a shard's loop, a node's
/// maintenance tick). A hashed timing wheel (Varghese &amp; Lauck 1987, ADR-058): <c>slots</c> lists of <c>tick</c>
/// each, an item in the slot of its due tick (later rounds stay there until their turn), so scheduling, cancelling and
/// firing are O(1) and allocate nothing once the entry table reached its peak. Not thread-safe: one owner thread (or
/// the owner's lock).
/// </summary>
public sealed class TimerWheel<T>
{
    private struct Entry
    {
        public T Item;
        public long Due;      // tick number
        public int Next, Prev; // slot list (-1: none); Next is the free list while free
        public int Slot;       // -1 while free
        public int Generation;
    }

    private readonly TimeProvider _time;
    private readonly long _tickTimestamps; // clock timestamp units per wheel tick
    private readonly int[] _heads;
    private Entry[] _entries = new Entry[64];
    private int _free = -1, _used;
    private long _current; // last tick advanced to
    private readonly long _origin;

    /// <param name="time">The clock (default: the system's); a manual one (tests) fires timers as it moves on.</param>
    public TimerWheel(TimeSpan tick, int slots = 512, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _tickTimestamps = Math.Max(1, (long)(tick.TotalSeconds * _time.TimestampFrequency));
        _heads = new int[slots];
        Array.Fill(_heads, -1);
    }

    /// <summary>Timers scheduled and not yet fired or cancelled.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Fire <paramref name="item"/> once <paramref name="delay"/> has passed: never earlier, at most two ticks later (the
    /// current tick is partly over, and the delay rounds up to whole ticks).
    /// </summary>
    /// <returns>A handle for <see cref="Cancel"/> (its top 8 bits are 0: owners may tag handles there).</returns>
    public long Schedule(TimeSpan delay, in T item)
    {
        int index = Allocate();
        ref var e = ref _entries[index];
        e.Item = item;
        long ticks = (long)Math.Ceiling(Math.Max(0, delay.TotalSeconds) * _time.TimestampFrequency / _tickTimestamps);
        e.Due = Math.Max(TickOf(_time.GetTimestamp()), _current) + ticks + 1;
        Link(index);
        Count++;
        return (long)((e.Generation + _salt) & GenerationMask) << 32 | (uint)index;
    }

    /// <summary>Cancel a scheduled timer; false when it already fired, was cancelled or is another wheel's.</summary>
    public bool Cancel(long handle) => Cancel(handle, out _);

    /// <summary>Cancel a scheduled timer and get its item back (to release what it holds).</summary>
    public bool Cancel(long handle, out T item)
    {
        item = default!;
        int index = (int)(uint)handle;
        if ((uint)index >= (uint)_used) return false;
        ref var e = ref _entries[index];
        if (e.Slot < 0 || ((e.Generation + _salt) & GenerationMask) != (int)(handle >> 32 & GenerationMask)) return false;
        item = e.Item;
        Unlink(index);
        Release(index);
        return true;
    }

    /// <summary>Fire every timer due by now, in due order per tick. A handler may schedule and cancel timers.</summary>
    public int Advance<THandler>(ref THandler handler) where THandler : ITimerHandler<T>
    {
        long now = TickOf(_time.GetTimestamp());
        int fired = 0;
        while (_current < now)
        {
            long tick = ++_current;
            int slot = (int)(tick % _heads.Length);
            for (int index = _heads[slot]; index >= 0;)
            {
                int next = _entries[index].Next;
                if (_entries[index].Due <= tick)
                {
                    var item = _entries[index].Item;
                    Unlink(index);
                    Release(index);
                    handler.OnTimer(in item);
                    fired++;
                    if (next >= 0 && _entries[next].Slot != slot) next = _heads[slot]; // the handler cancelled it: rescan
                }

                index = next;
            }
        }

        return fired;
    }

    /// <summary>Hand every scheduled timer to <paramref name="handler"/> without waiting for it, and empty the wheel (shutdown).</summary>
    public void Clear<THandler>(ref THandler handler) where THandler : ITimerHandler<T>
    {
        for (int slot = 0; slot < _heads.Length; slot++)
            while (_heads[slot] >= 0)
            {
                int index = _heads[slot];
                var item = _entries[index].Item;
                Unlink(index);
                Release(index);
                handler.OnTimer(in item);
            }
    }

    private const int GenerationMask = 0xFF_FFFF;

    // Handles of this wheel start at a random generation: a handle kept from another wheel (an actor that moved to
    // another node) matches an entry here with odds of 2^-24, instead of the same index's first generation.
    private readonly int _salt = Random.Shared.Next(GenerationMask + 1);

    private long TickOf(long timestamp) => (timestamp - _origin) / _tickTimestamps;

    private int Allocate()
    {
        if (_free >= 0)
        {
            int index = _free;
            _free = _entries[index].Next;
            return index;
        }

        if (_used == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        return _used++;
    }

    private void Release(int index)
    {
        ref var e = ref _entries[index];
        e.Item = default!;
        e.Slot = -1;
        e.Generation++;
        e.Next = _free;
        _free = index;
        Count--;
    }

    private void Link(int index)
    {
        ref var e = ref _entries[index];
        int slot = (int)(e.Due % _heads.Length);
        e.Slot = slot;
        e.Prev = -1;
        e.Next = _heads[slot];
        if (e.Next >= 0) _entries[e.Next].Prev = index;
        _heads[slot] = index;
    }

    private void Unlink(int index)
    {
        ref var e = ref _entries[index];
        if (e.Prev >= 0) _entries[e.Prev].Next = e.Next;
        else _heads[e.Slot] = e.Next;
        if (e.Next >= 0) _entries[e.Next].Prev = e.Prev;
    }
}
