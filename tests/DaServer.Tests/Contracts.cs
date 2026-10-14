using System;
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
}

public interface IEcho : IService
{
    ValueTask<string> Echo(string text);
    ValueTask<ulong> NodeId();
}

[ClientAccessible]
public interface IAuth : IService
{
    ValueTask<bool> Login(long playerId);
}

[ClientAccessible]
public interface IPlayer : IActor
{
    ValueTask<int> AddGold(int amount);
    ValueTask JoinRoom(long roomId);
    void Say(long roomId, string text);
}

public interface IPlayerClient : IClientCallback
{
    void OnChat(long from, string text);
    void OnGold(int gold);
}

public record struct CounterState(int Value) : IComponent;

/// <summary>Tag: the counter grows by one every tick (written by the actor, processed by GrowthSystem).</summary>
public struct Growing : IComponent;

public sealed class Counter : Actor<CounterState>, ICounter
{
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
}

/// <summary>Per-tick logic over every growing counter of the shard (actors only write the Growing tag).</summary>
public sealed partial class GrowthSystem : EcsSystem
{
    [ForEach, With<Growing>]
    private static void Grow(ref CounterState counter) => counter.Value++;
}

public sealed class EchoService : Service, IEcho
{
    public ValueTask<string> Echo(string text) => new(text);

    public ValueTask<ulong> NodeId() => new(Cluster.Id);
}

public sealed class AuthService : Service, IAuth
{
    public ValueTask<bool> Login(long playerId)
    {
        if (!Caller.IsValid || playerId <= 0) return new(false);
        Cluster.BindSession(Caller, playerId);
        return new(true);
    }
}

public record struct Wallet(int Gold) : IComponent;

public sealed class Player : Actor<Wallet>, IPlayer
{
    public ValueTask<int> AddGold(int amount)
    {
        int gold = State.Gold += amount;
        if (Caller.IsValid) Cluster.PlayerClient(Caller).OnGold(gold); // push to the calling client
        return new(gold);
    }

    public ValueTask JoinRoom(long roomId)
    {
        Cluster.JoinGroup(Caller, new ClientGroup(roomId));
        return default;
    }

    public void Say(long roomId, string text) => Cluster.PlayerClient(new ClientGroup(roomId)).OnChat(Key, text);
}
