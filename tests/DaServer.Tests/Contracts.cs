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

// The "business code" the tests drive: contracts, implementations and systems written exactly as users write them.

[NinoType]
public partial record struct Point(int X, int Y);

[Actor]
public interface ICounter
{
    ValueTask<int> Add(int delta);
    ValueTask<int> Get();
    void Bump();                                        // one-way
    ValueTask Reset();
    ValueTask<string> WhereAmI();                       // shard thread name
    ValueTask<Point> Echo(Point point);                 // Nino-serialized argument and result
    ValueTask<int> AddVia(long otherKey, int delta);    // actor -> actor call
    ValueTask Fail(string message);
    ValueTask<int> SlowAdd(int delta);                  // awaits a foreign Task.Delay, resumes on the shard
    ValueTask Grow();                                   // input for GrowthSystem
    ValueTask<long> AddMany(long otherKey, int count);  // sequential calls; returns bytes allocated meanwhile
    ValueTask<long> PublishMany(long otherKey, int count); // keyed events; returns bytes allocated meanwhile
}

[Service]
public interface IEcho
{
    ValueTask<string> Echo(string text);
    ValueTask<ulong> NodeId();
}

[Service, FromClient]
public interface IAuth
{
    ValueTask<bool> Login(long playerId);
    ValueTask<bool> LoginAfterPushes(long playerId, int pushes); // binds behind a burst of 8 KB pushes to the caller
    ValueTask Logout();
}

[Actor, FromClient]
public interface IPlayer
{
    ValueTask<int> AddGold(int amount);
    void Tip(int amount);                               // one-way
}

/// <summary>A public chat room: clients choose the room id.</summary>
[Actor, FromClient(AnyKey = true)]
public interface IRoom
{
    ValueTask Join();
    void Say(string text);
}

/// <summary>An actor that unloads when idle (its state lives only while it is loaded).</summary>
[Actor]
public interface IVisitor
{
    ValueTask<int> Visit();
}

/// <summary>Keyed event: delivered to the counter activation of <see cref="Key"/>.</summary>
[NinoType]
public readonly partial record struct CounterBumped([property: ActorKey] long Key, int By);

/// <summary>Unkeyed event: delivered once to each service implementation handling it.</summary>
[NinoType]
public readonly partial record struct Ping(int Id);

[ToClient]
public interface IPlayerClient
{
    void OnChat(long from, string text);
    void OnGold(int gold);
}

public record struct CounterState(int Value) : IComponent;

/// <summary>Tag: the counter grows by one every tick (written by the actor, processed by GrowthSystem).</summary>
public struct Growing : IComponent;

public sealed class Counter : Actor<CounterState>, ICounter
{
    [Handle]
    public ValueTask On(CounterBumped e)
    {
        State.Value += e.By;
        return default;
    }

    public ValueTask<int> Add(int delta) => new(State.Value += delta);

    public ValueTask<int> Get() => new(State.Value);

    public void Bump() => State.Value++;

    public ValueTask Reset()
    {
        State.Value = 0;
        return default;
    }

    public ValueTask<string> WhereAmI() => new(Thread.CurrentThread.Name ?? "");

    public ValueTask<Point> Echo(Point point) => new(point with { X = point.X + 1 });

    public async ValueTask<int> AddVia(long otherKey, int delta)
    {
        int other = await Cluster.Counter(otherKey).Add(delta);
        return State.Value += other; // back on this shard's thread: no locks needed
    }

    public ValueTask Fail(string message) => throw new InvalidOperationException(message);

    public async ValueTask<int> SlowAdd(int delta)
    {
        var shard = Shard.Current;
        await Task.Delay(10);
        if (Shard.Current != shard) throw new InvalidOperationException("continuation left the shard thread");
        return State.Value += delta;
    }

    public ValueTask Grow()
    {
        Entity.GetOrAdd<Growing>();
        return default;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> AddMany(long otherKey, int count)
    {
        var other = Cluster.Counter(otherKey);
        for (int i = 0; i < 100; i++) await other.Add(1); // warm-up: pools, JIT
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < count; i++) await other.Add(1);
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> PublishMany(long otherKey, int count)
    {
        // Bursts of 100 events, each followed by a call (events and calls of one sender reach an actor in order):
        // steady state with bounded frames in flight, as in a running server.
        const int burst = 100;
        var other = Cluster.Counter(otherKey);
        for (int i = 0; i < burst; i++) Cluster.Publish(new CounterBumped(otherKey, 1));
        await other.Get();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int sent = 0; sent < count; sent += burst)
        {
            for (int i = 0; i < burst; i++) Cluster.Publish(new CounterBumped(otherKey, 1));
            await other.Get();
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

public record struct Visits(int Count) : IComponent;

public sealed class Visitor : Actor<Visits>, IVisitor
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

    public ValueTask<int> Visit() => new(++State.Count);
}

/// <summary>What event handlers observed (tests read it from the test thread).</summary>
public static class Observed
{
    public static readonly ConcurrentQueue<string> Events = new();
}

public sealed class EchoService : Service, IEcho
{
    public ValueTask<string> Echo(string text) => new(text);

    public ValueTask<ulong> NodeId() => new(Cluster.NodeId);

    [Handle]
    public ValueTask On(Ping e)
    {
        Observed.Events.Enqueue($"echo:{e.Id}:{Cluster.NodeId:x}");
        return default;
    }
}

/// <summary>A second service handling Ping: every handling implementation receives each event once.</summary>
public sealed class AuditService : Service
{
    [Handle]
    public ValueTask On(Ping e)
    {
        Observed.Events.Enqueue($"audit:{e.Id}");
        return default;
    }
}

public sealed class AuthService : Service, IAuth
{
    public ValueTask<bool> Login(long playerId)
    {
        if (!CallerSession.IsValid || playerId <= 0) return new(false);
        Cluster.Bind<IPlayer>(CallerSession, playerId);
        return new(true);
    }

    public ValueTask<bool> LoginAfterPushes(long playerId, int pushes)
    {
        var text = new string('x', 4096);
        for (int i = 0; i < pushes; i++) Cluster.PlayerClient(CallerSession).OnChat(0, text);
        return Login(playerId);
    }

    public ValueTask Logout()
    {
        Cluster.Unbind<IPlayer>(CallerSession);
        return default;
    }
}

public record struct Wallet(int Gold) : IComponent;

public sealed class Player : Actor<Wallet>, IPlayer
{
    public ValueTask<int> AddGold(int amount)
    {
        int gold = State.Gold += amount;
        if (CallerSession.IsValid) Cluster.PlayerClient(CallerSession).OnGold(gold); // push to the calling client
        return new(gold);
    }

    public void Tip(int amount) => State.Gold += amount;

    [Handle]
    public ValueTask On(SessionClosed e)
    {
        Observed.Events.Enqueue($"closed:{e.Key}");
        return default;
    }
}

public sealed class Room : Actor, IRoom
{
    public ValueTask Join()
    {
        Cluster.JoinGroup(CallerSession, Group);
        return default;
    }

    public void Say(string text) => Cluster.PlayerClient(Group).OnChat(Key, text);
}
