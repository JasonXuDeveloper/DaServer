namespace DaServer;

/// <summary>
/// Objects pooled per thread: rented and returned where they are used (a shard thread, which owns its calls and their
/// continuations), so without synchronization, and as many as that thread's peak in flight. Bounded: objects returned on
/// another thread (a completion on a thread-pool thread) do not pile up there. The process has three kinds of pools:
/// this one for objects a thread both rents and returns; <c>ObjectPool</c> (DaServer.Core) for objects rented on one
/// thread and returned on another (frames, calls completed on IO threads), with per-thread magazines; and a shard's
/// own per-shard state as ECS singletons and entities (asks in flight, replies kept for later).
/// </summary>
internal static class PerThread<T> where T : class
{
    private const int Bound = 1 << 14;

    [ThreadStatic] private static Stack<T>? t_items;

    public static bool TryRent([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? item)
    {
        if (t_items is { Count: > 0 } items)
        {
            item = items.Pop();
            return true;
        }

        item = null;
        return false;
    }

    public static void Return(T item)
    {
        var items = t_items ??= new Stack<T>();
        if (items.Count < Bound) items.Push(item);
    }
}
