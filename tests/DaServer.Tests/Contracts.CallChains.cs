using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- call chains (actors asking each other)

public readonly record struct LoopId(long Value) : IActorId;

/// <summary>
/// Ask <see cref="Path"/>'s first actor to ask the rest in turn (<see cref="ViaService"/>: through
/// <see cref="LoopService"/>); the last one answers with its index. A path may come back to an actor whose own ask is
/// still out: nothing waits in a handler (ADR-081), so the actor takes the hop and the chain completes.
/// </summary>
[NinoType, Message] public partial record struct Hop(long[] Path, int At, bool ViaService) : IRequest<int>;

/// <summary>Answered <see cref="Ms"/> later with <see cref="Ms"/> (a delayed send carries the kept reply); the actor takes other mail meanwhile.</summary>
[NinoType, Message] public partial record struct Hold(int Ms) : IRequest<int>;

/// <summary>
/// A reply kept as data comes due: <see cref="Value"/> to the request kept with <c>ReplyLater</c> (its token's entity and
/// call), or a failure when <see cref="Fail"/>. Sent by an actor to itself with <c>SendAfter</c>.
/// </summary>
[NinoType, Message] public partial record struct ReplyDue(int Pending, int Version, uint Call, int Value, bool Fail)
{
    public static ReplyDue Of(ReplyTo to, int value, bool fail = false) => new(to.Pending.Index, to.Pending.Version, to.CallId, value, fail);

    public ReplyTo To() => new(new EntityId(Pending, Version), Call);
}

[Actor<LoopId>]
public static class Looper
{
    /// <summary>Keys whose <see cref="Created"/> asks the actor 1,000 below them (the one whose request loads them).</summary>
    public static bool AsksBackOnCreate(long key) => key is >= 2_000 and < 3_000;

    [Handle]
    internal static void On(in Created _, in Context ctx)
    {
        if (AsksBackOnCreate(ctx.Key)) ctx.Ask(new LoopId(ctx.Key - 1_000), new Hold(0));
    }

    [Handle] internal static void On(in Answered<Hold, int> a, in Context ctx) => Observed.Events.Enqueue($"asked-back:{ctx.Key}:{a.Status}");

    [Handle]
    internal static Answer<int> On(in Hop m, in Context ctx)
    {
        if (m.At == m.Path.Length - 1) return m.At;
        var next = m with { At = m.At + 1 };
        if (m.ViaService) ctx.Ask(new PassOn(next), ctx.ReplyLater());
        else ctx.Ask(new LoopId(m.Path[next.At]), next, ctx.ReplyLater());
        return Answer<int>.Later;
    }

    [Handle] internal static void On(in Answered<Hop, int> a, in Context ctx) => Pass(a.Status, a.Value, a.ReplyTo, in ctx);

    [Handle] internal static void On(in Answered<PassOn, int> a, in Context ctx) => Pass(a.Status, a.Value, a.ReplyTo, in ctx);

    [Handle]
    internal static Answer<int> On(in Hold m, in Context ctx)
    {
        if (m.Ms == 0) return 0;
        ctx.SendAfter(TimeSpan.FromMilliseconds(m.Ms), ctx.Id<LoopId>(), ReplyDue.Of(ctx.ReplyLater(), m.Ms));
        return Answer<int>.Later;
    }

    [Handle] internal static void On(in ReplyDue m, in Context ctx) => ctx.Reply(m.To(), m.Value);

    /// <summary>The time its caller still waits, as the handler reads it.</summary>
    [Handle] internal static long On(in HowLong _, in Context ctx) => ctx.Deadline - Environment.TickCount64;

    /// <summary>An answer down the chain is the reply up the chain (its failure too).</summary>
    internal static void Pass(RpcStatus status, int value, ReplyTo to, in Context ctx)
    {
        if (status == RpcStatus.Ok) ctx.Reply(to, value);
        else ctx.Fail(to, status);
    }
}

/// <summary>A hop through <see cref="LoopService"/>.</summary>
[NinoType, Message] public partial record struct PassOn(Hop Hop) : IRequest<int>;

/// <summary>Passes a hop on: a service in the middle of a chain replies when the rest of the chain answered.</summary>
[Service]
public static class LoopService
{
    [Handle]
    internal static Answer<int> On(in PassOn m, in Context ctx)
    {
        ctx.Ask(new LoopId(m.Hop.Path[m.Hop.At]), m.Hop, ctx.ReplyLater());
        return Answer<int>.Later;
    }

    [Handle] internal static void On(in Answered<Hop, int> a, in Context ctx) => Looper.Pass(a.Status, a.Value, a.ReplyTo, in ctx);
}
