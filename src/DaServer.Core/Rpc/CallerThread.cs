namespace DaServer.Rpc;

/// <summary>
/// A thread that completes the calls made on it itself (a server shard): their replies are handed to it and their
/// continuations run there inline. Calls made on any other thread complete on the thread pool.
/// </summary>
internal abstract class CallerThread
{
    [ThreadStatic] private static CallerThread? _current;

    /// <summary>The caller thread running the current code, or null.</summary>
    public static CallerThread? Current => _current;

    /// <summary>Mark the calling thread as this one (null: none) while it runs.</summary>
    protected static void SetCurrent(CallerThread? thread) => _current = thread;
}
