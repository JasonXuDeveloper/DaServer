namespace DaServer.Concurrency;

/// <summary>
/// At most one log line per interval for an error that can repeat at volume (a system failing every pass, a link down
/// under load), and how many were left out since the last one: the count is kept, the flood is not. A mutable struct:
/// keep it in a field and call it there.
/// </summary>
internal struct LogThrottle(long intervalMs)
{
    private long _loggedMs = long.MinValue / 2;
    private int _unlogged;

    /// <summary>Whether to log now, for one owner (a shard thread, or under a lock); <paramref name="unlogged"/>: left out since the last.</summary>
    public bool Due(out int unlogged)
    {
        long now = Platform.Milliseconds;
        if (now - _loggedMs < intervalMs)
        {
            _unlogged++;
            unlogged = 0;
            return false;
        }

        unlogged = _unlogged;
        (_loggedMs, _unlogged) = (now, 0);
        return true;
    }

    /// <summary><see cref="Due"/> for callers on any thread: one of those racing in an interval logs.</summary>
    public bool DueOnAnyThread(out int unlogged)
    {
        long now = Platform.Milliseconds, last = Volatile.Read(ref _loggedMs);
        if (now - last < intervalMs || Interlocked.CompareExchange(ref _loggedMs, now, last) != last)
        {
            Interlocked.Increment(ref _unlogged);
            unlogged = 0;
            return false;
        }

        unlogged = Interlocked.Exchange(ref _unlogged, 0);
        return true;
    }
}
