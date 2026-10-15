using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// The "business code" the tests drive: messages, handlers and systems written exactly as users write them.

[NinoType] public partial record struct Point(int X, int Y);

// ---------------------------------------------------------------- counter (actor)

[NinoType] public partial record struct Add(int Delta) : IRequest<int>;
[NinoType] public partial record struct Get : IRequest<int>;
[NinoType] public partial record struct Bump;                                       // one-way
[NinoType] public partial record struct Reset : IRequest<bool>;
[NinoType] public partial record struct WhereAmI : IRequest<string>;              // shard thread name
[NinoType] public partial record struct EchoPoint(Point Point) : IRequest<Point>;
[NinoType] public partial record struct AddVia(long Other, int Delta) : IRequest<int>; // actor -> actor request
[NinoType] public partial record struct Fail(string Message) : IRequest<bool>;
[NinoType] public partial record struct SlowAdd(int Delta) : IRequest<int>;       // awaits a foreign Task.Delay
[NinoType] public partial record struct Grow : IRequest<bool>;                    // input for GrowthSystem
[NinoType] public partial record struct AddMany(long Other, int Count) : IRequest<long>;     // bytes allocated meanwhile
[NinoType] public partial record struct PublishMany(long Other, int Count) : IRequest<long>; // bytes allocated meanwhile

/// <summary>Event: delivered to the counter of the key it is published with.</summary>
[NinoType] public partial record struct CounterBumped(int By);

public record struct CounterState(int Value) : IComponent;

/// <summary>Tag: the counter grows by one every tick (written by the actor, processed by GrowthSystem).</summary>
public struct Growing : IComponent;

public sealed class Counter : Actor<CounterState>
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
    public async ValueTask<long> On(PublishMany m)
    {
        // Bursts of 100 events, each followed by a request (messages of one sender reach an actor in order): steady
        // state with bounded frames in flight, as in a running server.
        const int burst = 100;
        for (int i = 0; i < burst; i++) Cluster.Publish(m.Other, new CounterBumped(1));
        await Cluster.Ask(m.Other, new Get());
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int sent = 0; sent < m.Count; sent += burst)
        {
            for (int i = 0; i < burst; i++) Cluster.Publish(m.Other, new CounterBumped(1));
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
[NinoType] public partial record struct Unhandled : IRequest<int>;

// ---------------------------------------------------------------- visitor (actor unloaded when idle)

[NinoType] public partial record struct Visit : IRequest<int>;

public record struct Visits(int Count) : IComponent;

public sealed class Visitor : Actor<Visits>
{
    public override TimeSpan UnloadAfterIdle => TimeSpan.FromMilliseconds(300);

    public override ValueTask OnLoad()
    {
        Observed.Events.Enqueue($"load:{Key}");
        return default;
    }

    public override ValueTask OnUnload()
    {
        Observed.Events.Enqueue($"unload:{Key}");
        return default;
    }

    [Handle] public int On(Visit _) => ++State.Count;
}

/// <summary>What handlers observed (tests read it from the test thread).</summary>
public static class Observed
{
    public static readonly ConcurrentQueue<string> Events = new();
}

// ---------------------------------------------------------------- services

[NinoType] public partial record struct EchoText(string Text) : IRequest<string>;
[NinoType] public partial record struct WhichNode : IRequest<ulong>;

/// <summary>Event: delivered once to each service type handling it.</summary>
[NinoType] public partial record struct Ping(int Id);

public sealed class EchoService : Service
{
    [Handle] public string On(EchoText m) => m.Text;

    [Handle] public ulong On(WhichNode _) => Cluster.NodeId;

    [Handle] public void On(Ping e) => Observed.Events.Enqueue($"echo:{e.Id}:{Cluster.NodeId:x}");
}

/// <summary>A second service handling Ping: every handling implementation receives each event once.</summary>
public sealed class AuditService : Service
{
    [Handle] public void On(Ping e) => Observed.Events.Enqueue($"audit:{e.Id}");
}

// ---------------------------------------------------------------- client-facing: login, player, rooms

[NinoType, ClientMessage] public partial record struct Login(long PlayerId) : IRequest<bool>;

/// <summary>Logs in behind a burst of 4 KB pushes to the caller (the binding must not be overtaken).</summary>
[NinoType, ClientMessage] public partial record struct LoginAfterPushes(long PlayerId, int Pushes) : IRequest<bool>;

[NinoType, ClientMessage] public partial record struct Logout : IRequest<bool>;
[NinoType, ClientMessage] public partial record struct AddGold(int Amount) : IRequest<int>;
[NinoType, ClientMessage] public partial record struct Tip(int Amount);            // one-way

/// <summary>Server-only: the login service attaches a client session to its player.</summary>
[NinoType] public partial record struct Attach(SessionRef Session) : IRequest<bool>;

/// <summary>A public chat room: clients choose the room id.</summary>
[NinoType, ClientMessage(AnyKey = true)] public partial record struct Join : IRequest<bool>;
[NinoType, ClientMessage(AnyKey = true)] public partial record struct Say(string Text);

// Pushes (server to client).
[NinoType] public partial record struct Chat(long From, string Text);
[NinoType] public partial record struct GoldChanged(int Gold);

public sealed class AuthService : Service
{
    [Handle]
    public async ValueTask<bool> On(Login m) =>
        CallerSession.IsValid && m.PlayerId > 0 && await Cluster.Ask(m.PlayerId, new Attach(CallerSession));

    [Handle]
    public ValueTask<bool> On(LoginAfterPushes m)
    {
        var text = new string('x', 2048); // 4 KB (UTF-16)
        for (int i = 0; i < m.Pushes; i++) Cluster.Send(CallerSession, new Chat(0, text));
        return On(new Login(m.PlayerId));
    }
}

public record struct Wallet(int Gold) : IComponent;

public sealed class Player : Actor<Wallet>
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
        await UnbindSession(CallerSession);
        return true;
    }

    [Handle]
    public int On(AddGold m)
    {
        int gold = State.Gold += m.Amount;
        if (CallerSession.IsValid) Cluster.Send(CallerSession, new GoldChanged(gold)); // push to the calling client
        return gold;
    }

    [Handle] public void On(Tip m) => State.Gold += m.Amount;

    [Handle] public void On(SessionClosed _) => Observed.Events.Enqueue($"closed:{Key}");
}

public sealed class Room : Actor
{
    [Handle]
    public bool On(Join _)
    {
        Cluster.JoinGroup(CallerSession, Group);
        return true;
    }

    [Handle] public void On(Say m) => Cluster.Send(Group, new Chat(Key, m.Text));
}
