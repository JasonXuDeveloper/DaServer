using System;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- contracts (what users write)

[NinoType]
public partial record struct Point(int X, int Y);

public interface ICounter : IActor
{
    ValueTask<int> Add(int delta);
    ValueTask<int> Get();
    void Bump();                                   // one-way
    ValueTask Reset();
    ValueTask<string> WhereAmI();                  // shard thread name
    ValueTask<Point> Echo(Point point);
    ValueTask<int> AddVia(long otherKey, int delta);  // actor -> actor call (awaits on the shard thread)
    ValueTask Fail(string message);
    ValueTask<int> SlowAdd(int delta);             // awaits a foreign Task.Delay, must resume on the shard
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

// ---------------------------------------------------------------- implementations

public struct CounterState : IComponent
{
    public int Value;
}

public struct Wallet : IComponent
{
    public int Gold;
    public SessionRef Session;
}

public sealed class Counter : Actor, ICounter
{
    public override ValueTask OnActivateAsync()
    {
        Add<CounterState>();
        return default;
    }

    public ValueTask<int> Add(int delta) => new(Get<CounterState>().Value += delta);

    public ValueTask<int> Get() => new(Get<CounterState>().Value);

    public void Bump() => Get<CounterState>().Value++;

    public ValueTask Reset()
    {
        Get<CounterState>().Value = 0;
        return default;
    }

    public ValueTask<string> WhereAmI() => new(Thread.CurrentThread.Name ?? "");

    public ValueTask<Point> Echo(Point point) => new(point with { X = point.X + 1 });

    public async ValueTask<int> AddVia(long otherKey, int delta)
    {
        int other = await Runtime.Counter(otherKey).Add(delta);
        // Back on this shard's thread: state access is safe without locks.
        return Get<CounterState>().Value += other;
    }

    public ValueTask Fail(string message) => throw new InvalidOperationException(message);

    public async ValueTask<int> SlowAdd(int delta)
    {
        string before = Thread.CurrentThread.Name ?? "";
        await Task.Delay(10);
        if ((Thread.CurrentThread.Name ?? "") != before)
            throw new InvalidOperationException("continuation left the shard thread");
        return Get<CounterState>().Value += delta;
    }
}

public sealed class EchoService : Service, IEcho
{
    public ValueTask<string> Echo(string text) => new(text);

    public ValueTask<ulong> NodeId() => new(Runtime.Id);
}

public sealed class AuthService : Service, IAuth
{
    public ValueTask<bool> Login(long playerId)
    {
        var caller = CallContext.Current.Caller;
        if (!caller.IsValid || playerId <= 0) return new(false);
        Runtime.BindSession(caller, playerId);
        return new(true);
    }
}

public sealed class Player : Actor, IPlayer
{
    public ValueTask<int> AddGold(int amount)
    {
        ref var wallet = ref Has<Wallet>() ? ref Get<Wallet>() : ref Add<Wallet>();
        wallet.Gold += amount;
        wallet.Session = CallContext.Current.Caller;
        Runtime.PlayerClient(wallet.Session).OnGold(wallet.Gold);   // push to this client
        return new(wallet.Gold);
    }

    public ValueTask JoinRoom(long roomId)
    {
        Runtime.JoinGroup(CallContext.Current.Caller, roomId);
        return default;
    }

    public void Say(long roomId, string text) => Runtime.PlayerClientGroup(roomId).OnChat(Key, text);
}

/// <summary>ECS system running in every shard: counts ticks of every counter actor.</summary>
public sealed partial class TickCounterSystem : SystemBase
{
    public static int Ticks;

    protected override void OnUpdate() => Interlocked.Increment(ref Ticks);
}
