using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- guest (loads and unloads all the time)

[NinoType, Message] public partial record struct Knock;

/// <summary>An actor that unloads as soon as the idle scan sees it a second after its last mail (actor churn: logins, short matches).</summary>
[Actor<GuestId>(UnloadAfterIdleSeconds = 1)]
public static class Guest
{
    [Handle] internal static void On(in Knock _, ref Visits visits) => Interlocked.Increment(ref Observed.Knocks);
}

/// <summary>What handlers observed (tests read it from the test thread).</summary>
public static class Observed
{
    public static readonly ConcurrentQueue<string> Events = new();

    /// <summary><see cref="Knock"/>s handled by guests (counted without allocating).</summary>
    public static long Knocks;

    /// <summary>Sum of what the tally services received.</summary>
    public static long Tallies;

    /// <summary>Set by the counter whose ask timed out: whether the answer ran on a shard thread (<see cref="AskAndFail"/>).</summary>
    public static TaskCompletionSource<bool> ResumedOnShard = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// ---------------------------------------------------------------- stalled requests (calls in flight)

/// <summary>A client request answered only once the test releases it (<see cref="ReleaseStalls"/>).</summary>
[NinoType, Message, FromClient] public partial record struct Stall : IRequest<bool>;

/// <summary>Held by the <see cref="Staller"/> until released.</summary>
[NinoType, Message] public partial record struct HoldStall : IRequest<bool>;

/// <summary>Answer every held stall (the test sends it).</summary>
[NinoType, Message] public partial record struct ReleaseStalls : IRequest<int>;

public readonly record struct StallerId(long Value) : IActorId;

/// <summary>Passes a client's stall to the <see cref="Staller"/>, which holds it: the client's call stays in flight.</summary>
[Service]
public static class StallService
{
    [Handle]
    internal static Answer<bool> On(in Stall _, in Context ctx)
    {
        ctx.Ask(Staller.One, new HoldStall(), ctx.ReplyLater());
        return Answer<bool>.Later;
    }

    [Handle]
    internal static void On(in Answered<HoldStall, bool> a, in Context ctx)
    {
        if (a.Ok) ctx.Reply(a.ReplyTo, a.Value);
        else ctx.Fail(a.ReplyTo, a.Status);
    }
}

/// <summary>Keeps the stalls it is asked to hold as reply tokens (data) until <see cref="ReleaseStalls"/>.</summary>
[Actor<StallerId>]
public static class Staller
{
    public static readonly StallerId One = new(1);

    [Handle]
    internal static Answer<bool> On(in HoldStall _, ref DynamicBuffer<HeldReply> held, in Context ctx)
    {
        var to = ctx.ReplyLater();
        held.Add(new HeldReply(to.Pending, to.CallId));
        return Answer<bool>.Later;
    }

    [Handle]
    internal static int On(in ReleaseStalls _, ref DynamicBuffer<HeldReply> held, in Context ctx)
    {
        int released = 0;
        foreach (var stall in held.AsReadOnlySpan())
            if (ctx.Reply(new ReplyTo(stall.Pending, stall.Call), true)) released++;
        held.Clear();
        return released;
    }
}
