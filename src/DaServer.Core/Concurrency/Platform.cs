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

    /// <summary>The number of leading zero bits of <paramref name="value"/> (32 for 0): one instruction on .NET.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroCount(uint value)
    {
#if NET
        return System.Numerics.BitOperations.LeadingZeroCount(value);
#else
        int n = 32;
        for (; value != 0; value >>= 1) n--;
        return n;
#endif
    }

    /// <summary>
    /// The processor this thread runs on (<see cref="Thread.GetCurrentProcessorId"/> on .NET, cached by the runtime),
    /// else its thread id: what spreads threads over the stripes of a shared structure. Any value is correct.
    /// </summary>
    public static int ProcessorId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET
        get => Thread.GetCurrentProcessorId();
#else
        get => Environment.CurrentManagedThreadId;
#endif
    }

#if !NET
    [ThreadStatic] private static Random? t_random;
#endif

    /// <summary>A uniform random number in [0, 1), thread-safe: <see cref="Random.Shared"/> on .NET.</summary>
    public static double NextDouble()
    {
#if NET
        return Random.Shared.NextDouble();
#else
        return (t_random ??= new Random(Environment.CurrentManagedThreadId ^ Environment.TickCount)).NextDouble();
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
