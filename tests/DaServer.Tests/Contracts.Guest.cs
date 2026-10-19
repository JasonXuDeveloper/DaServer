using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- guest (loads and unloads all the time)

[NinoType, Message] public partial record struct Knock;

/// <summary>An actor that unloads as soon as the idle scan sees it (actor churn: logins, short matches).</summary>
public sealed class Guest : Actor<GuestId, Visits>
{
    public override TimeSpan UnloadAfterIdle => TimeSpan.FromMilliseconds(1);

    [Handle] public void On(Knock _) => Interlocked.Increment(ref Observed.Knocks);
}

/// <summary>What handlers observed (tests read it from the test thread).</summary>
public static class Observed
{
    public static readonly ConcurrentQueue<string> Events = new();

    /// <summary><see cref="Knock"/>s handled by guests (counted without allocating).</summary>
    public static long Knocks;

    /// <summary>Sum of what the tally services received.</summary>
    public static long Tallies;

    /// <summary>What <see cref="Stall"/> requests wait for (reset by the test using it).</summary>
    public static TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A client request answered only once the test releases it (calls in flight).</summary>
[NinoType, Message, FromClient] public partial record struct Stall : IRequest<bool>;

public sealed class StallService : Service
{
    [Handle] public async Reply<bool> On(Stall _) => await Observed.Release.Task;
}
