using System.Runtime.CompilerServices;
using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

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

public readonly record struct TangleId(long Value) : IActorId;

/// <summary>Tie the tangle's knot to itself and ask for a critical write: its record no longer serializes.</summary>
[NinoType, Message] public partial record struct Tie : IRequest<bool>;

/// <summary>An actor whose state can stop serializing (<see cref="Tie"/>).</summary>
public sealed class Tangle : Actor<TangleId, Knotted>
{
    [Handle]
    public bool On(Tie _)
    {
        var knot = new Knot();
        knot.Next = knot;
        State.Knot = knot;
        Entity.Add<SaveNow>();
        return true;
    }
}

public readonly record struct BrittleId(long Value) : IActorId;

[NinoType, Message] public partial record struct Poke : IRequest<int>;

/// <summary>An actor without a record whose construction or state initializer can throw (user code at load).</summary>
public sealed class Brittle : Actor<BrittleId, Brittleness>
{
    public static readonly StrongBox<int> ConstructorThrows = new();

    public Brittle()
    {
        if (ConstructorThrows.Value == 1) throw new InvalidOperationException("a constructor threw");
    }

    [Handle] public int On(Poke _) => ++State.Value;
}

/// <summary>
/// Keys below 100 checkpoint every 100 ms, keys from 100 only on unload (or with SaveNow); keys from 1,000 to 1,999 also
/// unload after 50 ms without mail; keys from 2,000 reload after a failed turn.
/// </summary>
public sealed class Bank : Actor<BankId, Ledger>
{
    /// <summary>While 1, a new bank's constructor throws (user code in an actor's construction).</summary>
    public static readonly StrongBox<int> ConstructorThrows = new();

    public Bank()
    {
        if (ConstructorThrows.Value == 1) throw new InvalidOperationException("a constructor threw");
    }

    public override TimeSpan SaveEvery => SettingsThrow.Value == 1 && Key >= 8000 ? throw new InvalidOperationException("a setting threw")
        : Key < 100 ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero;

    /// <summary>While 1, <see cref="SaveEvery"/> throws for keys from 8,000 (user code in an actor's settings).</summary>
    public static readonly StrongBox<int> SettingsThrow = new();

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
