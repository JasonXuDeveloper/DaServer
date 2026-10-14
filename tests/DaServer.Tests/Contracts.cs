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

public interface ICounter : IActor
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

public interface IEcho : IService
{
    ValueTask<string> Echo(string text);
    ValueTask<ulong> NodeId();
}

public interface IAuth : IPublicService
{
    ValueTask<bool> Login(long playerId);
    ValueTask Logout();
}

public interface IPlayer : ISessionActor
{
    ValueTask<int> AddGold(int amount);
}

/// <summary>A public chat room: clients choose the room id.</summary>
public interface IRoom : IPublicActor
{
    ValueTask Join();
    void Say(string text);
}

/// <summary>Keyed event: delivered to the counter activation of <see cref="Key"/>.</summary>
[NinoType]
public readonly partial record struct CounterBumped(long Key, int By) : IKeyedEvent;

/// <summary>Unkeyed event: delivered once to each service implementation handling it.</summary>
[NinoType]
public readonly partial record struct Ping(int Id) : IEvent;

public interface IPlayerClient : IClientCallback
{
    void OnChat(long from, string text);
    void OnGold(int gold);
}

public record struct CounterState(int Value) : IComponent;

/// <summary>Tag: the counter grows by one every tick (written by the actor, processed by GrowthSystem).</summary>
public struct Growing : IComponent;

public sealed class Counter : Actor<CounterState>, ICounter, IHandle<CounterBumped>
{
    public ValueTask Handle(CounterBumped e)
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

/// <summary>What event handlers observed (tests read it from the test thread).</summary>
public static class Observed
{
    public static readonly ConcurrentQueue<string> Events = new();
}

public sealed class EchoService : Service, IEcho, IHandle<Ping>
{
    public ValueTask<string> Echo(string text) => new(text);

    public ValueTask<ulong> NodeId() => new(Cluster.NodeId);

    public ValueTask Handle(Ping e)
    {
        Observed.Events.Enqueue($"echo:{e.Id}:{Cluster.NodeId:x}");
        return default;
    }
}

/// <summary>A second service handling Ping: every handling implementation receives each event once.</summary>
public sealed class AuditService : Service, IHandle<Ping>
{
    public ValueTask Handle(Ping e)
    {
        Observed.Events.Enqueue($"audit:{e.Id}");
        return default;
    }
}

public sealed class AuthService : Service, IAuth
{
    public ValueTask<bool> Login(long playerId)
    {
        if (!Caller.IsValid || playerId <= 0) return new(false);
        Cluster.Bind<IPlayer>(Caller, playerId);
        return new(true);
    }

    public ValueTask Logout()
    {
        Cluster.Unbind<IPlayer>(Caller);
        return default;
    }
}

public record struct Wallet(int Gold) : IComponent;

public sealed class Player : Actor<Wallet>, IPlayer, IHandle<SessionClosed>
{
    public ValueTask<int> AddGold(int amount)
    {
        int gold = State.Gold += amount;
        if (Caller.IsValid) Cluster.PlayerClient(Caller).OnGold(gold); // push to the calling client
        return new(gold);
    }

    public ValueTask Handle(SessionClosed e)
    {
        Observed.Events.Enqueue($"closed:{e.Key}");
        return default;
    }
}

public sealed class Room : Actor, IRoom
{
    public ValueTask Join()
    {
        Cluster.JoinGroup(Caller, Group);
        return default;
    }

    public void Say(string text) => Cluster.PlayerClient(Group).OnChat(Key, text);
}
