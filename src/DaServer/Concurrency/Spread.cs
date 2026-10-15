namespace DaServer.Concurrency;

/// <summary>
/// Round robin without a shared counter: each thread counts on its own (seeded by its id, so threads start apart).
/// Spreading work needs no exact global order, and a counter every message thread increments would be the hottest
/// contended cache line of the node.
/// </summary>
internal static class Spread
{
    [ThreadStatic] private static uint _next;

    public static uint Next()
    {
        uint next = _next;
        if (next == 0) next = (uint)Environment.CurrentManagedThreadId * 0x9E3779B9u | 1;
        _next = next + 1;
        return next;
    }
}
