namespace DaServer.Concurrency;

/// <summary>
/// Exponential backoff with full jitter: attempt n waits a uniform time in [0, min(cap, first x 2^n)], so peers that
/// failed together (a node restarting, a load balancer dropping its connections) do not all retry at the same moment.
/// </summary>
internal struct Backoff(TimeSpan first, TimeSpan cap)
{
    private int _attempt;

    /// <summary>The wait before the next attempt.</summary>
    public TimeSpan Next()
    {
        double ceiling = Math.Min(cap.TotalMilliseconds, first.TotalMilliseconds * (1L << Math.Min(_attempt++, 30)));
        return TimeSpan.FromMilliseconds(Platform.NextDouble() * ceiling);
    }
}
