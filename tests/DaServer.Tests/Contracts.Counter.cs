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
[NinoType, Message] public partial record struct SlowAdd(int Delta) : IRequest<int>;       // awaits a foreign Task.Delay
[NinoType, Message] public partial record struct Grow : IRequest<bool>;                    // input for GrowthSystem
[NinoType, Message] public partial record struct AnswerLater(int Ms, bool Fail) : IRequest<int>;
[NinoType, Message] public partial record struct BumpLater(CounterId Target, int Ms, int By) : IRequest<long>; // SendAfter: the timer
[NinoType, Message] public partial record struct CancelBump(long Timer) : IRequest<bool>;
[NinoType, Message] public partial record struct BumpManyLater(CounterId Target, int Count) : IRequest<bool>;
[NinoType, Message] public partial record struct ConfirmVia(CounterId Target) : IRequest<int>; // replied when Target answered (ReplyWhen)
[NinoType, Message] public partial record struct ConfirmViaMany(CounterId Via, CounterId Target, int Count) : IRequest<long>; // bytes allocated meanwhile // replied after Ms, the turn ends at once
[NinoType, Message] public partial record struct AskAndFail(CounterId Other);            // completes Reports.ResumedOnShard
[NinoType, Message] public partial record struct AddMany(CounterId Other, int Count) : IRequest<long>;     // bytes allocated meanwhile
[NinoType, Message] public partial record struct SendMany(CounterId Other, int Count) : IRequest<long>;    // bytes allocated meanwhile
[NinoType, Message] public partial record struct AddViaMany(CounterId Via, CounterId Target, int Count) : IRequest<long>; // bytes allocated meanwhile
[NinoType, Message] public partial record struct AskInBursts(CounterId Other, int Width, int Rounds) : IRequest<long>; // bytes allocated meanwhile
[NinoType, Message] public partial record struct SuspendInBursts(long FirstVia, CounterId Target, int Width, int Rounds) : IRequest<long>; // bytes allocated meanwhile

/// <summary>One-way: sent in bursts (allocation test, events test).</summary>
[NinoType, Message] public partial record struct CounterBumped(int By);


/// <summary>What handlers report to the test that drives them.</summary>
public static class Reports
{
    /// <summary>Whether the last <see cref="AskAndFail"/> resumed on its shard after its call failed.</summary>
    public static TaskCompletionSource<bool> ResumedOnShard = new();
}


public sealed class Counter : Actor<CounterId, CounterState>
{
    [Handle] public void On(CounterBumped e) => State.Value += e.By;

    [Handle] public int On(Add m) => State.Value += m.Delta;

    [Handle] public int On(Get _) => State.Value;

    [Handle] public void On(Bump _) => State.Value++;

    [Handle]
    public bool On(Reset _)
    {
        State.Value = 0;
        return true;
    }

    [Handle] public string On(WhereAmI _) => Thread.CurrentThread.Name ?? "";

    [Handle] public Point On(EchoPoint m) => m.Point with { X = m.Point.X + 1 };

    [Handle]
    public async Reply<int> On(AddVia m)
    {
        int other = await Cluster.Ask(m.Other, new Add(m.Delta));
        return State.Value += other; // back on this shard's thread: no locks needed
    }

    [Handle] public bool On(Fail m) => throw new InvalidOperationException(m.Message);

    [Handle]
    public async Reply<int> On(SlowAdd m)
    {
        var shard = Shard.Current;
#pragma warning disable DAS009 // on purpose: a foreign await must resume on the shard
        await Task.Delay(10);
#pragma warning restore DAS009
        if (Shard.Current != shard) throw new InvalidOperationException("continuation left the shard thread");
        return State.Value += m.Delta;
    }

    [Handle]
    public async Reply On(AskAndFail m)
    {
        var shard = Shard.Current;
        try
        {
            await Cluster.Ask(m.Other, new Add(1));
        }
        catch (RpcException)
        {
        }

        Reports.ResumedOnShard.TrySetResult(Shard.Current == shard);
    }

    [Handle] public long On(BumpLater m) => Cluster.SendAfter(TimeSpan.FromMilliseconds(m.Ms), m.Target, new CounterBumped(m.By));

    [Handle] public bool On(CancelBump m) => Cluster.CancelSend(m.Timer);

    [Handle]
    public bool On(BumpManyLater m)
    {
        for (int i = 0; i < m.Count; i++) Cluster.SendAfter(TimeSpan.Zero, m.Target, new CounterBumped(1));
        return true;
    }

    [Handle] public Reply<int> On(ConfirmVia m) => ReplyWhen(Cluster.Ask(m.Target, new Add(1)), State.Value);

    [Handle]
    public async Reply<long> On(ConfirmViaMany m)
    {
        for (int i = 0; i < 100; i++) await Cluster.Ask(m.Via, new ConfirmVia(m.Target)); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Cluster.Ask(m.Via, new ConfirmVia(m.Target));
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    [Handle]
    public Reply<int> On(AnswerLater m) =>
#pragma warning disable DAS009 // on purpose: a foreign task completing later, off the shard
        ReplyWhen(new Reply(m.Fail ? ValueTask.FromException(new InvalidOperationException("later")) : new ValueTask(Task.Delay(m.Ms))), State.Value);
#pragma warning restore DAS009

    [Handle]
    public bool On(Grow _)
    {
        Entity.GetOrAdd<Growing>();
        return true;
    }

    [Handle]
    public async Reply<long> On(AddMany m)
    {
        for (int i = 0; i < 100; i++) await Cluster.Ask(m.Other, new Add(1)); // warm-up: pools, JIT
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Cluster.Ask(m.Other, new Add(1));
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    [Handle]
    public async Reply<long> On(AddViaMany m)
    {
        // Every call suspends in the callee (it awaits a third actor), so each reply goes through the reply watcher.
        for (int i = 0; i < 100; i++) await Cluster.Ask(m.Via, new AddVia(m.Target, 1)); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Cluster.Ask(m.Via, new AddVia(m.Target, 1));
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    [Handle]
    public async Reply<long> On(AskInBursts m)
    {
        // Width calls in flight at once, then all awaited: more than any fixed pool size must not allocate when warm.
        var pending = new Reply<int>[m.Width];
        await Burst(Cluster, m, pending); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int round = 0; round < m.Rounds; round++) await Burst(Cluster, m, pending);
        return GC.GetTotalAllocatedBytes(precise: true) - before;

        static async Reply Burst(ICluster cluster, AskInBursts m, Reply<int>[] pending)
        {
            for (int i = 0; i < pending.Length; i++) pending[i] = cluster.Ask(m.Other, new Add(1));
            for (int i = 0; i < pending.Length; i++) await pending[i];
        }
    }

    [Handle]
    public async Reply<long> On(SuspendInBursts m)
    {
        // Width actors at once each suspend in an async handler (AddVia awaits Target): Width boxes alive together.
        var pending = new Reply<int>[m.Width];
        await Burst(Cluster, m, pending); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int round = 0; round < m.Rounds; round++) await Burst(Cluster, m, pending);
        return GC.GetTotalAllocatedBytes(precise: true) - before;

        static async Reply Burst(ICluster cluster, SuspendInBursts m, Reply<int>[] pending)
        {
            for (int i = 0; i < pending.Length; i++) pending[i] = cluster.Ask(new CounterId(m.FirstVia + i), new AddVia(m.Target, 1));
            for (int i = 0; i < pending.Length; i++) await pending[i];
        }
    }

    [Handle]
    public async Reply<long> On(ServiceTraffic m)
    {
        // Each round: a one-way message, a published event (two services) and a request to services; the request
        // returns after the others on the same path (one shard queue locally, one ordered link remotely).
        for (int i = 0; i < 100; i++) await Round(Cluster); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Round(Cluster);
        return GC.GetTotalAllocatedBytes(precise: true) - before;

        static Reply<int> Round(ICluster cluster)
        {
            cluster.Send(new Tally(1));
            cluster.Publish(new Tallied(1));
            return cluster.Ask(new TallyAsk(1));
        }
    }

    [Handle]
    public async Reply<long> On(SendMany m)
    {
        // Bursts of 100 one-way messages, each followed by a request (messages of one sender reach an actor in order):
        // steady state with bounded frames in flight, as in a running server.
        const int burst = 100;
        for (int i = 0; i < burst; i++) Cluster.Send(m.Other, new CounterBumped(1));
        await Cluster.Ask(m.Other, new Get());
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int sent = 0; sent < m.Count; sent += burst)
        {
            for (int i = 0; i < burst; i++) Cluster.Send(m.Other, new CounterBumped(1));
            await Cluster.Ask(m.Other, new Get());
        }

        return GC.GetTotalAllocatedBytes(precise: true) - before;
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
    private static void Tally(in Ledger _, in ActorRef self, World world)
    {
        if (self.Actor.Key >= 7000) Ticks.AddOrUpdate((world.Shard.Node.Id, self.Actor.Key), 1, static (_, n) => n + 1);
    }
}

/// <summary>Released by tests holding a turn (<see cref="HoldTurn"/>).</summary>
public static class TurnGate
{
    public static TaskCompletionSource Current = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A request no type handles.</summary>
[NinoType, Message] public partial record struct Unhandled : IRequest<int>;

/// <summary>A request two service types answer (never sent: only <c>RouteCheck</c> looks at it).</summary>
[NinoType, Message] public partial record struct AnsweredTwice : IRequest<int>;

public sealed class FirstAnswer : Service
{
    [Handle] public int On(AnsweredTwice _) => 1;
}

public sealed class SecondAnswer : Service
{
    [Handle] public int On(AnsweredTwice _) => 2;
}
