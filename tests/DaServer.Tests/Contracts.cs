using System.Runtime.CompilerServices;
using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// The "business code" the tests drive: messages, handlers and systems written exactly as users write them.

[NinoType] public partial record struct Point(int X, int Y);

public readonly record struct CounterId(long Value) : IActorId;
public readonly record struct VisitorId(long Value) : IActorId;
public readonly record struct GuestId(long Value) : IActorId;
public readonly record struct PlayerId(long Value) : IActorId;
public readonly record struct RoomId(long Value) : IActorId;

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

// ---------------------------------------------------------------- visitor (actor unloaded when idle)

[NinoType, Message] public partial record struct Visit : IRequest<int>;


public sealed class Visitor : Actor<VisitorId, Visits>
{
    public override TimeSpan UnloadAfterIdle => TimeSpan.FromMilliseconds(300);

    public override Reply OnLoad()
    {
        Observed.Events.Enqueue($"load:{Id.Value}");
        return default;
    }

    public override Reply OnUnload()
    {
        Observed.Events.Enqueue($"unload:{Id.Value}");
        return default;
    }

    [Handle] public int On(Visit _) => ++State.Count;
}

/// <summary>A visitor that is never unloaded for idleness: only removal paths unload it.</summary>
public readonly record struct KeeperId(long Value) : IActorId;

public sealed class Keeper : Actor<KeeperId, Visits>
{
    public override Reply OnUnload()
    {
        Observed.Events.Enqueue($"keeper-unload:{Id.Value}");
        return default;
    }

    [Handle] public int On(Visit _) => ++State.Count;
}

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

// ---------------------------------------------------------------- persistence (ADR-068)

public readonly record struct BankId(long Value) : IActorId;

[NinoType, Message] public partial record struct Deposit(int Gold, bool Critical) : IRequest<int>;
[NinoType, Message] public partial record struct Balance : IRequest<int>;
[NinoType, Message] public partial record struct DepositCount : IRequest<int>;
[NinoType, Message] public partial record struct DepositTwice(int Gold, BankId Other) : IRequest<int>;
[NinoType, Message] public partial record struct Earn : IRequest<bool>;
[NinoType, Message] public partial record struct Hit : IRequest<int>;
[NinoType, Message] public partial record struct Overdraw(int Gold, bool Async) : IRequest<int>;
/// <summary>A turn that waits for <see cref="TurnGate"/>: the actor cannot unload meanwhile.</summary>
[NinoType, Message] public partial record struct HoldTurn : IRequest<int>;
/// <summary>Takes gold, waits for <see cref="TurnGate"/>, then fails: the half-changed state must not be kept.</summary>
[NinoType, Message] public partial record struct FailAfterGate(int Gold) : IRequest<int>;

/// <summary>
/// Keys below 100 checkpoint every 100 ms, keys from 100 only on unload (or with SaveNow); keys from 1,000 to 1,999 also
/// unload after 50 ms without mail; keys from 2,000 reload after a failed turn.
/// </summary>
public sealed class Bank : Actor<BankId, Ledger>
{
    public override TimeSpan SaveEvery => Key < 100 ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero;

    public override TimeSpan UnloadAfterIdle => Key is >= 1000 and < 2000 ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero;

    public override ErrorAction OnError(Exception error) => Key >= 2000 ? ErrorAction.Reload : ErrorAction.Resume;

    /// <summary>Keys whose next OnLoad throws (once each).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> FailNextLoad = new();

    public override Reply OnLoad()
    {
        if (!FailNextLoad.TryRemove(Key, out _)) return default;
        State.Gold = -1; // half-way through, then it fails: this state must not be kept
        throw new InvalidOperationException("OnLoad failed");
    }

    /// <summary>A critical change that fails half-way (synchronously or after an await): nothing of it may be written.</summary>
    [Handle]
    public async Reply<int> On(Overdraw m)
    {
        State.Gold -= m.Gold;
        Entity.Add<SaveNow>();
        if (m.Async) await Cluster.Ask(new BankId(1), new Balance());
        throw new InvalidOperationException("overdrawn");
    }

    [Handle]
    public int On(Hit _)
    {
        if (!Entity.Has<Streak>()) Entity.Add<Streak>();
        return ++Entity.Get<Streak>().Count;
    }

    [Handle]
    public int On(Deposit m)
    {
        State.Gold += m.Gold;
        if (!Entity.Has<Deposits>()) Entity.Add<Deposits>();
        Entity.Get<Deposits>().Count++;
        if (m.Critical) Entity.Add<SaveNow>();
        return State.Gold;
    }

    /// <summary>A critical change in two steps with an await between them: the record holds both or neither.</summary>
    [Handle]
    public async Reply<int> On(DepositTwice m)
    {
        State.Gold += m.Gold;
        Entity.Add<SaveNow>();
        await Cluster.Ask(m.Other, new Balance());
        return State.Gold += m.Gold;
    }

    [Handle] public int On(Balance _) => State.Gold;

    [Handle]
    public async Reply<int> On(HoldTurn _)
    {
        await TurnGate.Current.Task;
        return State.Gold;
    }

    [Handle]
    public async Reply<int> On(FailAfterGate m)
    {
        State.Gold -= m.Gold;
        await TurnGate.Current.Task;
        throw new InvalidOperationException("failed half-way");
    }

    [Handle]
    public bool On(Earn _)
    {
        if (!Entity.Has<Earning>()) Entity.Add<Earning>();
        return true;
    }

    [Handle] public int On(DepositCount _) => Entity.Has<Deposits>() ? Entity.Get<Deposits>().Count : 0;
}

// ---------------------------------------------------------------- supervision (OnError)

public readonly record struct FragileId(long Value) : IActorId;


/// <summary>Add, then fail half-way when <see cref="Fail"/> (synchronously or after an await): the state is half-changed.</summary>
[NinoType, Message] public partial record struct Spoil(int Delta, bool Fail, bool Async) : IRequest<int>;

/// <summary>Keys from 100 reload after a failure, the others resume (the default).</summary>
public sealed class Fragile : Actor<FragileId, FragileState>
{
    [Handle]
    public async Reply<int> On(Spoil m)
    {
        State.Value += m.Delta;
        if (m.Async) await Cluster.Ask(new CounterId(Key), new Get());
        if (m.Fail) throw new InvalidOperationException("half-applied");
        return State.Value;
    }

    public override ErrorAction OnError(Exception error) => Key >= 100 ? ErrorAction.Reload : ErrorAction.Resume;
}

// ---------------------------------------------------------------- an actor type with a small mailbox

public readonly record struct NarrowId(long Value) : IActorId;

[NinoType, Message] public partial record struct SlowStep : IRequest<bool>;

public sealed class Narrow : Actor<NarrowId>
{
    public override int MailboxCapacity => 2;

    [Handle]
    public async Reply<bool> On(SlowStep _)
    {
#pragma warning disable DAS009 // on purpose: requests pile up behind a slow turn
        await Task.Delay(10);
#pragma warning restore DAS009
        return true;
    }
}

// ---------------------------------------------------------------- call chains (actors asking each other)

public readonly record struct LoopId(long Value) : IActorId;

/// <summary>
/// Ask <see cref="Path"/>'s first actor to ask the rest in turn (<see cref="ViaService"/>: through
/// <see cref="LoopService"/>); the last one answers. A path that returns to an actor still waiting is a call cycle.
/// </summary>
[NinoType, Message] public partial record struct Hop(long[] Path, int At, bool ViaService) : IRequest<int>;

/// <summary>Hold the turn for <see cref="Ms"/> (another caller's request queues behind it).</summary>
[NinoType, Message] public partial record struct Hold(int Ms) : IRequest<int>;

/// <summary>The actor's TimeLeft after an await of its own (it holds for the whole turn), in ms.</summary>
[NinoType, Message] public partial record struct HowLongAfterAwait : IRequest<long>;

public sealed class Looper : Actor<LoopId>
{
    /// <summary>Keys whose unload never finishes (a save that hangs): the stop budget test.</summary>
    public const long HangsOnUnload = 999;

    /// <summary>Keys whose OnLoad asks the actor 1,000 below them (the one whose request loads them: a cycle).</summary>
    public static bool AsksBackOnLoad(long key) => key is >= 2_000 and < 3_000;

    /// <summary>Keys that unload when idle, asking the actor 1,000 above them to ask them back (a cycle).</summary>
    public static bool AskedBackOnUnload(long key) => key is >= 3_000 and < 4_000;

    public override TimeSpan UnloadAfterIdle => AskedBackOnUnload(Key) ? TimeSpan.FromMilliseconds(100) : base.UnloadAfterIdle;

    public override async Reply OnLoad()
    {
        if (AsksBackOnLoad(Key)) await Cluster.Ask(new LoopId(Key - 1_000), new Hold(0));
    }

    public override async Reply OnUnload()
    {
#pragma warning disable DAS009 // on purpose: a save that does not finish within the stop budget
        if (Key == HangsOnUnload) await Task.Delay(Timeout.Infinite);
#pragma warning restore DAS009
        if (!AskedBackOnUnload(Key)) return;
        try
        {
            await Cluster.Ask(new LoopId(Key + 1_000), new Hop([Key + 1_000, Key], 0, ViaService: false));
        }
        finally
        {
            Observed.Events.Enqueue($"looper-unloaded:{Key}");
        }
    }

    [Handle]
    public async Reply<int> On(Hop m)
    {
        if (m.At == m.Path.Length - 1) return m.At;
        var next = m with { At = m.At + 1 };
        return m.ViaService ? await Cluster.Ask(new PassOn(next)) : await Cluster.Ask(new LoopId(m.Path[next.At]), next);
    }

    [Handle]
    public async Reply<long> On(HowLongAfterAwait _)
    {
        await Cluster.Ask(new LoopId(Key + 1), new Hold(50));
        return (long)TimeLeft.TotalMilliseconds;
    }

    [Handle]
    public async Reply<int> On(Hold m)
    {
#pragma warning disable DAS009 // on purpose: a turn that holds its actor
        await Task.Delay(m.Ms);
#pragma warning restore DAS009
        return m.Ms;
    }
}

/// <summary>A hop through <see cref="LoopService"/>.</summary>
[NinoType, Message] public partial record struct PassOn(Hop Hop) : IRequest<int>;

/// <summary>Passes a hop on: a service in the middle of a chain keeps it.</summary>
public sealed class LoopService : Service
{
    [Handle] public Reply<int> On(PassOn m) => Cluster.Ask(new LoopId(m.Hop.Path[m.Hop.At]), m.Hop);
}

// ---------------------------------------------------------------- services

[NinoType, Message] public partial record struct EchoText(string Text) : IRequest<string>;
[NinoType, Message] public partial record struct WhichNode : IRequest<ulong>;
[NinoType, Message] public partial record struct WhoAsks : IRequest<long>;                 // the caller's origin id
[NinoType, Message] public partial record struct Guarded(int N) : IRequest<int>;           // a dispatch filter refuses N < 0
[NinoType, Message] public partial record struct Screened(int N) : IRequest<int>;          // ScreenRules refuses N < 0
[NinoType, Message] public partial record struct HowLong : IRequest<long>;                  // the caller's TimeLeft, in ms

/// <summary>A [Filter]: registered by the generated registration, no startup code.</summary>
public static class ScreenRules
{
    [Filter] public static RpcStatus Screen(object target, in Screened message, in CallContext context) =>
        message.N < 0 ? RpcStatus.Forbidden : RpcStatus.Ok;
}

/// <summary>Event: delivered once to each service type handling it.</summary>
[NinoType, Message] public partial record struct Ping(int Id);

public sealed class EchoService : Service
{
    [Handle] public string On(EchoText m) => m.Text;

    [Handle] public ulong On(WhichNode _) => Cluster.NodeId;

    [Handle] public long On(WhoAsks _) => Caller.Id;

    [Handle] public int On(Guarded m) => m.N;

    [Handle] public int On(Screened m) => m.N;

    [Handle] public long On(HowLong _) => (long)TimeLeft.TotalMilliseconds;

    [Handle] public void On(Ping e) => Observed.Events.Enqueue($"echo:{e.Id}:{Cluster.NodeId:x}");
}

/// <summary>A second service handling Ping: every handler type receives each event once.</summary>
public sealed class AuditService : Service
{
    [Handle] public void On(Ping e) => Observed.Events.Enqueue($"audit:{e.Id}");
}

/// <summary>To the toy feature's addresses (FeatureTests).</summary>
[NinoType, Message] public partial record struct Probe(int N);

/// <summary>Service traffic without payload allocations (allocation tests): one-way, request and published event.</summary>
[NinoType, Message] public partial record struct Tally(int By);
[NinoType, Message] public partial record struct TallyAsk(int By) : IRequest<int>;
[NinoType, Message] public partial record struct Tallied(int By);

/// <summary>Drives <see cref="TallyService"/> from an actor: bytes allocated meanwhile.</summary>
[NinoType, Message] public partial record struct ServiceTraffic(int Count) : IRequest<long>;

public sealed class TallyService : Service
{
    [Handle] public void On(Tally m) => Interlocked.Add(ref Observed.Tallies, m.By);

    [Handle] public int On(TallyAsk m) => (int)Interlocked.Add(ref Observed.Tallies, m.By);

    [Handle] public void On(Tallied m) => Interlocked.Add(ref Observed.Tallies, m.By);
}

/// <summary>A second service handling <see cref="Tallied"/>: a publish reaches both.</summary>
public sealed class TallyAuditService : Service
{
    [Handle] public void On(Tallied m) => Interlocked.Add(ref Observed.Tallies, m.By);
}

// ---------------------------------------------------------------- client-facing: login, player, rooms

[NinoType, Message, FromClient] public partial record struct Login(PlayerId Player) : IRequest<bool>;

/// <summary>Logs in behind a burst of 4 KB pushes to the caller (the binding must not be overtaken).</summary>
[NinoType, Message, FromClient] public partial record struct LoginAfterPushes(PlayerId Player, int Pushes) : IRequest<bool>;

[NinoType, Message, FromClient] public partial record struct Logout : IRequest<bool>;
[NinoType, Message, FromClient] public partial record struct AddGold(int Amount) : IRequest<int>;
[NinoType, Message, FromClient] public partial record struct Tip(int Amount);            // one-way

/// <summary>The player holds its turn until <see cref="TurnGate"/> opens.</summary>
[NinoType, Message, FromClient] public partial record struct HoldPlayer : IRequest<bool>;

/// <summary>The player waits for <see cref="TurnGate"/>, then kicks the calling session.</summary>
[NinoType, Message, FromClient] public partial record struct KickAfterGate : IRequest<bool>;

/// <summary>Server-only: the login service attaches a client session to its player.</summary>
[NinoType, Message] public partial record struct Attach(ClientSession Session) : IRequest<bool>;

/// <summary>
/// A chat room: the lobby admits clients (<see cref="Enter"/>), the room grants their sessions, and they name the room
/// from then on (<c>client.Send(roomId, ..)</c>) until they <see cref="Exit"/>.
/// </summary>
[NinoType, Message, FromClient] public partial record struct Enter(RoomId Room) : IRequest<bool>;
[NinoType, Message] public partial record struct Admit(ClientSession Session) : IRequest<bool>;
[NinoType, Message, FromClient] public partial record struct Say(string Text);
[NinoType, Message, FromClient] public partial record struct Exit : IRequest<bool>;

// Pushes (server to client).
[NinoType, Message, ToClient] public partial record struct Chat(RoomId From, string Text);
[NinoType, Message, ToClient] public partial record struct GoldChanged(int Gold);

public sealed class LobbyService : Service
{
    [Handle]
    public async Reply<bool> On(Enter m) => m.Room.Value > 0 && await Cluster.Ask(m.Room, new Admit(Caller));
}

public sealed class AuthService : Service
{
    [Handle]
    public async Reply<bool> On(Login m) =>
        Caller.IsValid && m.Player.Value > 0 && await Cluster.Ask(m.Player, new Attach(Caller));

    [Handle]
    public Reply<bool> On(LoginAfterPushes m)
    {
        var text = new string('x', 2048); // 4 KB (UTF-16)
        for (int i = 0; i < m.Pushes; i++) Cluster.Send(Caller, new Chat(default, text));
        return On(new Login(m.Player));
    }
}


public sealed class Player : Actor<PlayerId, Wallet>
{
    [Handle]
    public async Reply<bool> On(Attach m)
    {
        await BindSession(m.Session);
        return true;
    }

    [Handle]
    public async Reply<bool> On(Logout _)
    {
        await UnbindSession(Caller);
        return true;
    }

    [Handle]
    public int On(AddGold m)
    {
        int gold = State.Gold += m.Amount;
        if (Caller.IsValid) Cluster.Send(Caller, new GoldChanged(gold)); // push to the calling client
        return gold;
    }

    [Handle] public void On(Tip m) => State.Gold += m.Amount;

    [Handle]
    public async Reply<bool> On(HoldPlayer _)
    {
        await TurnGate.Current.Task;
        return true;
    }

    [Handle]
    public async Reply<bool> On(KickAfterGate _)
    {
        await TurnGate.Current.Task;
        DisconnectSession(Caller);
        return true;
    }

    [Handle] public void On(SessionClosed _) => Observed.Events.Enqueue($"closed:{Id.Value}");

    [Handle] public void On(SessionResumed _) => Observed.Events.Enqueue($"resumed:{Id.Value}");
}

public sealed class Room : Actor<RoomId>
{
    [Handle]
    public async Reply<bool> On(Admit m)
    {
        await GrantSession(m.Session); // the session joins Group with the grant
        return true;
    }

    [Handle] public void On(Say m) => Cluster.Send(Group, new Chat(Id, m.Text));

    [Handle]
    public async Reply<bool> On(Exit _)
    {
        await RevokeSession(Caller);
        return true;
    }

    [Handle] public void On(SessionClosed _) => Observed.Events.Enqueue($"room-closed:{Id.Value}");

    [Handle] public void On(SessionResumed _) => Observed.Events.Enqueue($"room-resumed:{Id.Value}");
}

// ---------------------------------------------------------------- placement affinity (ADR-073)

/// <summary>A table, placed by its number.</summary>
public readonly record struct TableId(long Value) : IActorId, IPlaced
{
    public long Placement => Value;
}

/// <summary>A chair at a table: table number in the high bits, placed with its table.</summary>
public readonly record struct ChairId(long Value) : IActorId, IPlaced
{
    public static ChairId Of(long table, int chair) => new(table << 16 | (uint)chair);

    public long Placement => Value >> 16;
}

[NinoType, Message] public partial record struct WhereIsIt : IRequest<string>;

public sealed class Table : Actor<TableId>
{
    [Handle] public string On(WhereIsIt _) => Thread.CurrentThread.Name ?? "";
}

public sealed class Chair : Actor<ChairId>
{
    [Handle] public string On(WhereIsIt _) => Thread.CurrentThread.Name ?? "";
}

// ---------------------------------------------------------------- slot allocation (ADR-079)

public readonly record struct PinId(long Value) : IActorId;
public readonly record struct LoadId(long Value) : IActorId;

/// <summary>Counts its visits (its state moves with it).</summary>

/// <summary>Stays where it is loaded.</summary>
[Sticky]
public sealed class Pinned : Actor<PinId, Ledger>
{
    [Handle] public int On(Visit _) => ++State.Gold;
}

/// <summary>Evened out by load.</summary>
public sealed class Spread : Actor<LoadId, Ledger>
{
    [Handle] public int On(Visit _) => ++State.Gold;
}
