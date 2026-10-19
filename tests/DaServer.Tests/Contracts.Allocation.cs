using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- traffic drivers (allocation and tracing tests)

public readonly record struct DriverId(long Value) : IActorId;

/// <summary>What a <see cref="Drive"/> asks of its callees.</summary>
public enum DriveKind
{
    /// <summary><see cref="Add"/> of 1 to counter <c>Callee</c>.</summary>
    Add,

    /// <summary><see cref="AddVia"/>: counter <c>Callee + i</c> keeps the reply for later while it asks counter <c>Target</c>.</summary>
    AddVia,

    /// <summary><see cref="ConfirmVia"/>: counter <c>Callee + i</c> replies once counter <c>Target</c> answered (ReplyAfter).</summary>
    Confirm,

    /// <summary>One round: <c>Width</c> one-way <see cref="CounterBumped"/> to counter <c>Callee</c>, then one ask behind them.</summary>
    Bump,

    /// <summary>Per round: a message to a service, an event to two services and a request to one (<see cref="ServiceTraffic"/>).</summary>
    Service,
}

/// <summary>
/// Rounds of <see cref="Width"/> asks at once, each round once every answer of the last one is in (a chain of
/// <c>Answered</c> handlers: nothing waits); answered with the bytes the process allocated from the first ask to the last
/// answer.
/// </summary>
[NinoType, Message] public partial record struct Drive(DriveKind Kind, long Callee, long Target, int Width, int Rounds) : IRequest<long>;

/// <summary>Rounds of service traffic (<see cref="DriveKind.Service"/>), answered with the bytes allocated meanwhile.</summary>
[NinoType, Message] public partial record struct ServiceTraffic(int Rounds) : IRequest<long>;

[Actor<DriverId>]
public static class Driver
{
    [Handle] internal static Answer<long> On(in Drive m, ref DriverRun run, in Context ctx) => Start(m.Kind, m.Callee, m.Target, m.Width, m.Rounds, ref run, in ctx);

    [Handle] internal static Answer<long> On(in ServiceTraffic m, ref DriverRun run, in Context ctx) => Start(DriveKind.Service, 0, 0, 1, m.Rounds, ref run, in ctx);

    [Handle] internal static string On(in WhereAmI _) => Thread.CurrentThread.Name ?? "";

    [Handle] internal static void On(in Answered<Add, int> a, ref DriverRun run, in Context ctx) => Answered(a.Status, ref run, in ctx);

    [Handle] internal static void On(in Answered<AddVia, int> a, ref DriverRun run, in Context ctx) => Answered(a.Status, ref run, in ctx);

    [Handle] internal static void On(in Answered<ConfirmVia, int> a, ref DriverRun run, in Context ctx) => Answered(a.Status, ref run, in ctx);

    [Handle] internal static void On(in Answered<Get, int> a, ref DriverRun run, in Context ctx) => Answered(a.Status, ref run, in ctx);

    [Handle] internal static void On(in Answered<TallyAsk, int> a, ref DriverRun run, in Context ctx) => Answered(a.Status, ref run, in ctx);

    private static Answer<long> Start(DriveKind kind, long callee, long target, int width, int rounds, ref DriverRun run, in Context ctx)
    {
        var to = ctx.ReplyLater();
        run = new DriverRun(to.Pending, to.CallId, GC.GetTotalAllocatedBytes(precise: true), (int)kind, callee, target, width,
            kind == DriveKind.Bump ? 1 : rounds, 0);
        Launch(ref run, in ctx);
        return Answer<long>.Later;
    }

    /// <summary>One round of asks; the round ends when <see cref="DriverRun.InFlight"/> answers came back.</summary>
    private static void Launch(ref DriverRun run, in Context ctx)
    {
        run.InFlight = run.Kind == (int)DriveKind.Bump ? 1 : run.Width;
        var target = new CounterId(run.Target);
        switch ((DriveKind)run.Kind)
        {
            case DriveKind.Add:
                for (int i = 0; i < run.Width; i++) ctx.Ask(new CounterId(run.Callee), new Add(1));
                break;
            case DriveKind.AddVia:
                for (int i = 0; i < run.Width; i++) ctx.Ask(new CounterId(run.Callee + i), new AddVia(target, 1));
                break;
            case DriveKind.Confirm:
                for (int i = 0; i < run.Width; i++) ctx.Ask(new CounterId(run.Callee + i), new ConfirmVia(target));
                break;
            case DriveKind.Bump:
                for (int i = 0; i < run.Width; i++) ctx.Send(new CounterId(run.Callee), new CounterBumped(1));
                ctx.Ask(new CounterId(run.Callee), new Get()); // answered behind the bumps (one sender's mail stays in order)
                break;
            default:
                ctx.Send(new Tally(1));
                ctx.Publish(new Tallied(1));
                ctx.Ask(new TallyAsk(1));
                break;
        }
    }

    private static void Answered(RpcStatus status, ref DriverRun run, in Context ctx)
    {
        var to = new ReplyTo(run.Pending, run.Call);
        if (status != RpcStatus.Ok)
        {
            if (ctx.Fail(to, status)) run.Rounds = 0; // the first failure ends the run; later answers find no caller
            return;
        }

        if (--run.InFlight > 0) return;
        if (--run.Rounds > 0)
        {
            Launch(ref run, in ctx);
            return;
        }

        ctx.Reply(to, GC.GetTotalAllocatedBytes(precise: true) - run.StartBytes);
    }
}
