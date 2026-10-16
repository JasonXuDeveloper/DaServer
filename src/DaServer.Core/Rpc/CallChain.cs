namespace DaServer.Rpc;

/// <summary>
/// The call chain of the code running on this thread (ADR-059): set by the runtime around a turn's dispatch and around
/// each continuation of a call the turn awaited, read by <c>Ask</c>, which sends it with the request
/// (<see cref="FrameFlags.HasChain"/>). Every request a turn makes, directly or through the actors it asks, carries the
/// turn's chain, so one that reaches an actor whose running turn has that chain would wait for itself: it fails at once.
/// 0 outside turns (systems, timers, foreign awaits): such requests start a chain where they run.
/// </summary>
internal static class CallChain
{
    [ThreadStatic] private static long t_current;

    public static long Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => t_current = value;
    }
}
