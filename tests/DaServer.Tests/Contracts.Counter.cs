using System.Runtime.CompilerServices;
using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- counter (actor)

[NinoType, Message] public partial record struct Add(int Delta) : IRequest<int>;
[NinoType, Message, FromClient] public partial record struct Get : IRequest<int>;
[NinoType, Message] public partial record struct Bump;                                       // one-way
[NinoType, Message] public partial record struct Reset : IRequest<bool>;
[NinoType, Message] public partial record struct WhereAmI : IRequest<string>;              // shard thread name
[NinoType, Message] public partial record struct EchoPoint(Point Point) : IRequest<Point>;
[NinoType, Message] public partial record struct AddVia(CounterId Other, int Delta) : IRequest<int>; // actor -> actor request
[NinoType, Message] public partial record struct Fail(string Message) : IRequest<bool>;
[NinoType, Message] public partial record struct Grow : IRequest<bool>;                    // input for GrowthSystem
[NinoType, Message] public partial record struct BumpLater(CounterId Target, int Ms, int By) : IRequest<long>; // SendAfter: the timer
[NinoType, Message] public partial record struct CancelBump(long Timer) : IRequest<bool>;
[NinoType, Message] public partial record struct BumpManyLater(CounterId Target, int Count) : IRequest<bool>;
[NinoType, Message] public partial record struct ConfirmVia(CounterId Target) : IRequest<int>; // replied when Target answered (ReplyAfter)

/// <summary>Replied <see cref="Ms"/> later with the value the counter had now (or a failure): the counter takes other mail meanwhile.</summary>
[NinoType, Message] public partial record struct AnswerLater(int Ms, bool Fail) : IRequest<int>;
/// <summary>One-way: ask <see cref="Callee"/> and report where its failure is answered (<see cref="Observed.ResumedOnShard"/>).</summary>
[NinoType, Message] public partial record struct AskAndFail(CounterId Callee);

/// <summary>One-way: sent in bursts (allocation test, events test).</summary>
[NinoType, Message] public partial record struct CounterBumped(int By);


[Actor<CounterId>]
public static class Counter
{
    [Handle] internal static void On(in CounterBumped e, ref CounterState s) => s.Value += e.By;

    [Handle] internal static int On(in Add m, ref CounterState s) => s.Value += m.Delta;

    [Handle] internal static int On(in Get _, in CounterState s) => s.Value;

    [Handle] internal static void On(in Bump _, ref CounterState s) => s.Value++;

    [Handle]
    internal static bool On(in Reset _, ref CounterState s)
    {
        s.Value = 0;
        return true;
    }

    [Handle] internal static string On(in WhereAmI _) => Thread.CurrentThread.Name ?? "";

    [Handle] internal static Point On(in EchoPoint m) => m.Point with { X = m.Point.X + 1 };

    /// <summary>Ask another counter, then answer with this one's value plus its answer: the reply waits as data.</summary>
    [Handle]
    internal static Answer<int> On(in AddVia m, in Context ctx)
    {
        ctx.Ask(m.Other, new Add(m.Delta), ctx.ReplyLater());
        return Answer<int>.Later;
    }

    [Handle]
    internal static void On(in Answered<Add, int> a, ref CounterState s, in Context ctx)
    {
        if (a.Ok) ctx.Reply(a.ReplyTo, s.Value += a.Value);
        else ctx.Fail(a.ReplyTo, a.Status);
    }

    [Handle] internal static bool On(in Fail m) => throw new InvalidOperationException(m.Message);

    [Handle] internal static long On(in BumpLater m, in Context ctx) => ctx.SendAfter(TimeSpan.FromMilliseconds(m.Ms), m.Target, new CounterBumped(m.By));

    [Handle] internal static bool On(in CancelBump m, in Context ctx) => ctx.CancelSend(m.Timer);

    [Handle]
    internal static bool On(in BumpManyLater m, in Context ctx)
    {
        for (int i = 0; i < m.Count; i++) ctx.SendAfter(TimeSpan.Zero, m.Target, new CounterBumped(1));
        return true;
    }

    /// <summary>Answered with this counter's value once the target counter took one more.</summary>
    [Handle] internal static Answer<int> On(in ConfirmVia m, in CounterState s, in Context ctx) => ctx.ReplyAfter(ctx.Ask(m.Target, new Add(1)), s.Value);

    [Handle]
    internal static Answer<int> On(in AnswerLater m, in CounterState s, in Context ctx)
    {
        ctx.SendAfter(TimeSpan.FromMilliseconds(m.Ms), ctx.Id<CounterId>(), ReplyDue.Of(ctx.ReplyLater(), s.Value, m.Fail));
        return Answer<int>.Later;
    }

    [Handle]
    internal static void On(in ReplyDue m, in Context ctx)
    {
        if (m.Fail) ctx.Fail(m.To(), RpcStatus.Error);
        else ctx.Reply(m.To(), m.Value);
    }

    [Handle] internal static void On(in AskAndFail m, in Context ctx) => ctx.Ask(m.Callee, new Get());

    /// <summary>The answer of <see cref="AskAndFail"/>: its timeout is mail of this counter, on its shard's thread.</summary>
    [Handle]
    internal static void On(in Answered<Get, int> a, in Context ctx) =>
        Observed.ResumedOnShard.TrySetResult(a.Status == RpcStatus.Timeout && Shard.Current == ctx.World.Shard);

    [Handle]
    internal static bool On(in Grow _, Entity self)
    {
        self.GetOrAdd<Growing>();
        return true;
    }
}

/// <summary>Per-tick logic over every growing counter of the shard (actors only write the Growing tag).</summary>
public sealed partial class GrowthSystem : EcsSystem
{
    [ForEach, With<Growing>]
    private static void Grow(ref CounterState counter) => counter.Value++;
}

/// <summary>
/// A user system's bug, in the group the runtime's mail systems run in (before the turn, mailbox and persistence systems): it throws on every pass
/// over a growing counter while <see cref="Throwing"/> holds 1, and its creation throws while <see cref="CreateThrows"/>
/// does. The shard's other systems and actors must go on; a shard that cannot be built must read as stalled.
/// </summary>
[UpdateInGroup<ShardMessages>]
public sealed partial class FaultySystem : EcsSystem
{
    public static readonly StrongBox<int> Throwing = new(), CreateThrows = new();

    protected override void OnCreate()
    {
        if (CreateThrows.Value == 1) throw new InvalidOperationException("a system that cannot be created");
    }

    [ForEach, With<Growing>]
    private static void Trip(in CounterState _)
    {
        if (Throwing.Value == 1) throw new InvalidOperationException("a bug in a user system");
    }
}

/// <summary>Earning ledgers grow every tick: their records change between any two checkpoints.</summary>
public sealed partial class InterestSystem : EcsSystem
{
    [ForEach, With<Earning>]
    private static void Earn(ref Ledger ledger) => ledger.Gold++;
}

/// <summary>
/// Counts the ticks each live copy of a ledger with a streak (keys from 7,000) gets, per node: a copy that handed its
/// state to another node, or one still waiting for it, must get none.
/// </summary>
public sealed partial class TallySystem : EcsSystem
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Node, long Key), int> Ticks = new();

    [ForEach, With<Streak>]
    private static void Tally(in Ledger _, in ActorKey self, World world)
    {
        if (self.Key >= 7000) Ticks.AddOrUpdate((world.Shard.Node.Id, self.Key), 1, static (_, n) => n + 1);
    }
}

/// <summary>A request no type handles.</summary>
[NinoType, Message] public partial record struct Unhandled : IRequest<int>;

/// <summary>A request two service types answer (never sent: only <c>RouteCheck</c> looks at it).</summary>
[NinoType, Message] public partial record struct AnsweredTwice : IRequest<int>;

[Service]
public static class FirstAnswer
{
    [Handle] internal static int On(in AnsweredTwice _) => 1;
}

[Service]
public static class SecondAnswer
{
    [Handle] internal static int On(in AnsweredTwice _) => 2;
}
