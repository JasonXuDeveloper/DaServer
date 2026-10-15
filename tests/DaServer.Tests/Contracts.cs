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
[NinoType, Message(Flow.FromClient)] public partial record struct Get : IRequest<int>;
[NinoType, Message] public partial record struct Bump;                                       // one-way
[NinoType, Message] public partial record struct Reset : IRequest<bool>;
[NinoType, Message] public partial record struct WhereAmI : IRequest<string>;              // shard thread name
[NinoType, Message] public partial record struct EchoPoint(Point Point) : IRequest<Point>;
[NinoType, Message] public partial record struct AddVia(CounterId Other, int Delta) : IRequest<int>; // actor -> actor request
[NinoType, Message] public partial record struct Fail(string Message) : IRequest<bool>;
[NinoType, Message] public partial record struct SlowAdd(int Delta) : IRequest<int>;       // awaits a foreign Task.Delay
[NinoType, Message] public partial record struct Grow : IRequest<bool>;                    // input for GrowthSystem
[NinoType, Message] public partial record struct AddMany(CounterId Other, int Count) : IRequest<long>;     // bytes allocated meanwhile
[NinoType, Message] public partial record struct SendMany(CounterId Other, int Count) : IRequest<long>;    // bytes allocated meanwhile
[NinoType, Message] public partial record struct AddViaMany(CounterId Via, CounterId Target, int Count) : IRequest<long>; // bytes allocated meanwhile
[NinoType, Message] public partial record struct AskInBursts(CounterId Other, int Width, int Rounds) : IRequest<long>; // bytes allocated meanwhile

/// <summary>One-way: sent in bursts (allocation test, events test).</summary>
[NinoType, Message] public partial record struct CounterBumped(int By);

public record struct CounterState(int Value) : IComponent;

/// <summary>Tag: the counter grows by one every tick (written by the actor, processed by GrowthSystem).</summary>
public struct Growing : IComponent;

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
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<int> On(AddVia m)
    {
        int other = await Cluster.Ask(m.Other, new Add(m.Delta));
        return State.Value += other; // back on this shard's thread: no locks needed
    }

    [Handle] public bool On(Fail m) => throw new InvalidOperationException(m.Message);

    [Handle]
    public async ValueTask<int> On(SlowAdd m)
    {
        var shard = Shard.Current;
        await Task.Delay(10);
        if (Shard.Current != shard) throw new InvalidOperationException("continuation left the shard thread");
        return State.Value += m.Delta;
    }

    [Handle]
    public bool On(Grow _)
    {
        Entity.GetOrAdd<Growing>();
        return true;
    }

    [Handle]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> On(AddMany m)
    {
        for (int i = 0; i < 100; i++) await Cluster.Ask(m.Other, new Add(1)); // warm-up: pools, JIT
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Cluster.Ask(m.Other, new Add(1));
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    [Handle]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> On(AddViaMany m)
    {
        // Every call suspends in the callee (it awaits a third actor), so each reply goes through the reply watcher.
        for (int i = 0; i < 100; i++) await Cluster.Ask(m.Via, new AddVia(m.Target, 1)); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Cluster.Ask(m.Via, new AddVia(m.Target, 1));
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    [Handle]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> On(AskInBursts m)
    {
        // Width calls in flight at once, then all awaited: more than any fixed pool size must not allocate when warm.
        var pending = new ValueTask<int>[m.Width];
        await Burst(Cluster, m, pending); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int round = 0; round < m.Rounds; round++) await Burst(Cluster, m, pending);
        return GC.GetTotalAllocatedBytes(precise: true) - before;

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        static async ValueTask Burst(ICluster cluster, AskInBursts m, ValueTask<int>[] pending)
        {
            for (int i = 0; i < pending.Length; i++) pending[i] = cluster.Ask(m.Other, new Add(1));
            for (int i = 0; i < pending.Length; i++) await pending[i];
        }
    }

    [Handle]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> On(ServiceTraffic m)
    {
        // Each round: a one-way message, a published event (two services) and a request to services; the request
        // returns after the others on the same path (one shard queue locally, one ordered link remotely).
        for (int i = 0; i < 100; i++) await Round(Cluster); // warm-up
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < m.Count; i++) await Round(Cluster);
        return GC.GetTotalAllocatedBytes(precise: true) - before;

        static ValueTask<int> Round(ICluster cluster)
        {
            cluster.Send(new Tally(1));
            cluster.Publish(new Tallied(1));
            return cluster.Ask(new TallyAsk(1));
        }
    }

    [Handle]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> On(SendMany m)
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

/// <summary>A request no type handles.</summary>
[NinoType, Message] public partial record struct Unhandled : IRequest<int>;

// ---------------------------------------------------------------- visitor (actor unloaded when idle)

[NinoType, Message] public partial record struct Visit : IRequest<int>;

public record struct Visits(int Count) : IComponent;

public sealed class Visitor : Actor<VisitorId, Visits>
{
    public override TimeSpan UnloadAfterIdle => TimeSpan.FromMilliseconds(300);

    public override ValueTask OnLoad()
    {
        Observed.Events.Enqueue($"load:{Id.Value}");
        return default;
    }

    public override ValueTask OnUnload()
    {
        Observed.Events.Enqueue($"unload:{Id.Value}");
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
}

// ---------------------------------------------------------------- services

[NinoType, Message] public partial record struct EchoText(string Text) : IRequest<string>;
[NinoType, Message] public partial record struct WhichNode : IRequest<ulong>;

/// <summary>Event: delivered once to each service type handling it.</summary>
[NinoType, Message] public partial record struct Ping(int Id);

public sealed class EchoService : Service
{
    [Handle] public string On(EchoText m) => m.Text;

    [Handle] public ulong On(WhichNode _) => Cluster.NodeId;

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

[NinoType, Message(Flow.FromClient)] public partial record struct Login(PlayerId Player) : IRequest<bool>;

/// <summary>Logs in behind a burst of 4 KB pushes to the caller (the binding must not be overtaken).</summary>
[NinoType, Message(Flow.FromClient)] public partial record struct LoginAfterPushes(PlayerId Player, int Pushes) : IRequest<bool>;

[NinoType, Message(Flow.FromClient)] public partial record struct Logout : IRequest<bool>;
[NinoType, Message(Flow.FromClient)] public partial record struct AddGold(int Amount) : IRequest<int>;
[NinoType, Message(Flow.FromClient)] public partial record struct Tip(int Amount);            // one-way

/// <summary>Server-only: the login service attaches a client session to its player.</summary>
[NinoType, Message] public partial record struct Attach(SessionRef Session) : IRequest<bool>;

/// <summary>A public chat room: clients name the room (<c>client.Send(roomId, ..)</c>).</summary>
[NinoType, Message(Flow.FromClient, AnyId = true)] public partial record struct Join : IRequest<bool>;
[NinoType, Message(Flow.FromClient, AnyId = true)] public partial record struct Say(string Text);

// Pushes (server to client).
[NinoType, Message(Flow.ToClient)] public partial record struct Chat(RoomId From, string Text);
[NinoType, Message(Flow.ToClient)] public partial record struct GoldChanged(int Gold);

public sealed class AuthService : Service
{
    [Handle]
    public async ValueTask<bool> On(Login m) =>
        Caller.IsValid && m.Player.Value > 0 && await Cluster.Ask(m.Player, new Attach(Caller));

    [Handle]
    public ValueTask<bool> On(LoginAfterPushes m)
    {
        var text = new string('x', 2048); // 4 KB (UTF-16)
        for (int i = 0; i < m.Pushes; i++) Cluster.Send(Caller, new Chat(default, text));
        return On(new Login(m.Player));
    }
}

public record struct Wallet(int Gold) : IComponent;

public sealed class Player : Actor<PlayerId, Wallet>
{
    [Handle]
    public async ValueTask<bool> On(Attach m)
    {
        await BindSession(m.Session);
        return true;
    }

    [Handle]
    public async ValueTask<bool> On(Logout _)
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

    [Handle] public void On(SessionClosed _) => Observed.Events.Enqueue($"closed:{Id.Value}");

    [Handle] public void On(SessionResync _) => Observed.Events.Enqueue($"resync:{Id.Value}");
}

public sealed class Room : Actor<RoomId>
{
    [Handle]
    public bool On(Join _)
    {
        Cluster.Send(Caller, new JoinGroup(Group));
        return true;
    }

    [Handle] public void On(Say m) => Cluster.Send(Group, new Chat(Id, m.Text));
}
