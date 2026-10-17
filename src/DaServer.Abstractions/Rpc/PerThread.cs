namespace DaServer;

/// <summary>
/// Objects pooled per thread: rented and returned where they are used (a shard thread, which owns its calls and their
/// continuations), so without synchronization, and as many as that thread's peak in flight. Bounded: objects returned on
/// another thread (a completion on a thread-pool thread) do not pile up there.
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
