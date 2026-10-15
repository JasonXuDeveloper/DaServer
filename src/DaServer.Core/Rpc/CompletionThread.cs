namespace DaServer.Rpc;

/// <summary>
/// A thread that completes the calls made on it itself (a server shard): their replies are handed to it and their
/// continuations run there inline. Calls made on any other thread complete on the thread pool.
/// </summary>
internal abstract class CompletionThread
{
    [ThreadStatic] private static CompletionThread? _current;

    /// <summary>The caller thread running the current code, or null.</summary>
    public static CompletionThread? Current => _current;

    /// <summary>Mark the calling thread as this one (null: none) while it runs.</summary>
    protected static void SetCurrent(CompletionThread? thread) => _current = thread;

    /// <summary>Run <paramref name="callback"/> on this thread (thread-safe, never inline): a continuation of a call made here.</summary>
    public abstract void Post(Action<object?> callback, object? state);
}
