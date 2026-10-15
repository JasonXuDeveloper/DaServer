namespace DaServer.Concurrency;

/// <summary>
/// The few runtime services whose best API is newer than netstandard2.1 (Unity clients): on .NET the newer one, so
/// servers pay nothing for the shared code.
/// </summary>
internal static class Platform
{
#if !NET
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
#endif

    /// <summary>Milliseconds of a monotonic clock: <see cref="Environment.TickCount64"/> on .NET.</summary>
    public static long Milliseconds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET
        get => Environment.TickCount64;
#else
        get => Clock.ElapsedMilliseconds;
#endif
    }

    /// <summary>Run <paramref name="action"/> on the thread pool (cold paths: closing a connection off the caller's thread).</summary>
    public static void Queue<TState>(Action<TState> action, TState state) where TState : class
    {
#if NET
        ThreadPool.UnsafeQueueUserWorkItem(action, state, preferLocal: false);
#else
        ThreadPool.UnsafeQueueUserWorkItem(_ => action(state), null);
#endif
    }
}
