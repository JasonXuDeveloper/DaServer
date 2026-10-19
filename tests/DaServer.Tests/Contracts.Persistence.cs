using System.Runtime.CompilerServices;
using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- persistence (ADR-068)

[NinoType, Message] public partial record struct Deposit(int Gold, bool Critical) : IRequest<int>;
[NinoType, Message] public partial record struct Balance : IRequest<int>;
/// <summary>Sets the gold and asks for a write now: a state can change back to what the store last acknowledged.</summary>
[NinoType, Message] public partial record struct SetGold(int Gold) : IRequest<int>;
[NinoType, Message] public partial record struct DepositCount : IRequest<int>;
/// <summary>A critical deposit in two messages: the first half now, the second once <see cref="Other"/> answered.</summary>
[NinoType, Message] public partial record struct DepositTwice(int Gold, BankId Other) : IRequest<int>;
[NinoType, Message] public partial record struct Earn : IRequest<bool>;
[NinoType, Message] public partial record struct Hit : IRequest<int>;
/// <summary>
/// A critical change that fails half-way: in its own handler, or (<see cref="Async"/>) in the handler of the answer to
/// an ask it made first.
/// </summary>
[NinoType, Message] public partial record struct Overdraw(int Gold, bool Async) : IRequest<int>;
/// <summary>Seal the bank's record (a <see cref="Seal"/> component, which a node may be unable to read).</summary>
[NinoType, Message] public partial record struct Stamp : IRequest<bool>;

public readonly record struct TangleId(long Value) : IActorId;

/// <summary>Tie the tangle's knot to itself and ask for a critical write: its record no longer serializes.</summary>
[NinoType, Message] public partial record struct Tie : IRequest<bool>;

/// <summary>An actor whose state can stop serializing (<see cref="Tie"/>).</summary>
[Actor<TangleId>]
public static class Tangle
{
    [Handle]
    internal static bool On(in Tie _, ref Knotted knotted, Entity self)
    {
        var knot = new Knot();
        knot.Next = knot;
        knotted.Knot = knot;
        self.Set(new SaveNow()); // `knotted` is invalid from here on
        return true;
    }
}

public readonly record struct BrittleId(long Value) : IActorId;

[NinoType, Message] public partial record struct Poke : IRequest<int>;

/// <summary>An actor without a record whose first data comes from user code that can throw (its <see cref="Created"/>).</summary>
[Actor<BrittleId>]
public static class Brittle
{
    /// <summary>While 1, a new brittle's <see cref="Created"/> throws.</summary>
    public static readonly StrongBox<int> CreatedThrows = new();

    [Handle]
    internal static void On(in Created _, ref Brittleness state)
    {
        if (CreatedThrows.Value == 1) throw new InvalidOperationException("Created threw");
        state = new Brittleness(); // its initializer is user code too
    }

    [Handle] internal static int On(in Poke _, ref Brittleness state) => ++state.Value;
}

// Banks: one ledger behavior (Banking) with each persistence setting its own actor type, since settings are constants
// of the type (ADR-081).

public readonly record struct BankId(long Value) : IActorId;
public readonly record struct CheckpointedBankId(long Value) : IActorId;
public readonly record struct IdleBankId(long Value) : IActorId;
public readonly record struct ReloadingBankId(long Value) : IActorId;

/// <summary>Written on unload or when it asks (SaveNow) only; the bank of the handoff and persistence tests.</summary>
[Actor<BankId>(SaveEverySeconds = 0)]
public static class Bank
{
    /// <summary>Keys whose next <see cref="Loaded"/> throws (once each).</summary>
    public static readonly ConcurrentDictionary<long, bool> FailNextLoad = new();

    [Handle]
    internal static void On(in Loaded _, ref Ledger ledger, in Context ctx)
    {
        if (!FailNextLoad.TryRemove(ctx.Key, out bool _)) return;
        ledger.Gold = -1; // half-way through, then it fails: this state must not be kept
        throw new InvalidOperationException("Loaded failed");
    }

    [Handle] internal static int On(in Deposit m, ref Ledger ledger, ref Deposits deposits, Entity self) => Banking.Deposit(in m, ref ledger, ref deposits, self);

    [Handle] internal static int On(in Balance _, in Ledger ledger) => ledger.Gold;

    [Handle] internal static int On(in SetGold m, ref Ledger ledger, Entity self) => Banking.SetGold(in m, ref ledger, self);

    [Handle] internal static int On(in DepositCount _, Entity self) => self.TryGet<Deposits>(out var deposits) ? deposits.Count : 0;

    [Handle] internal static int On(in Hit _, ref Streak streak) => ++streak.Count;

    [Handle]
    internal static bool On(in Earn _, Entity self)
    {
        self.GetOrAdd<Earning>();
        return true;
    }

    [Handle]
    internal static bool On(in Stamp _, ref Seal seal)
    {
        seal.Mark = 1;
        return true;
    }

    [Handle]
    internal static Answer<int> On(in DepositTwice m, ref Ledger ledger, ref PendingDeposit pending, Entity self, in Context ctx)
    {
        ledger.Gold += m.Gold;
        pending.Gold = m.Gold; // the second half's amount: data for the answer's handler
        ctx.Ask(m.Other, new Balance(), ctx.ReplyLater());
        self.Set(new SaveNow()); // `ledger` and `pending` are invalid from here on
        return Answer<int>.Later;
    }

    [Handle]
    internal static void On(in Answered<Balance, int> a, ref Ledger ledger, ref PendingDeposit pending, Entity self, in Context ctx)
    {
        if (!a.Ok)
        {
            ctx.Fail(a.ReplyTo, a.Status);
            return;
        }

        int gold = ledger.Gold += pending.Gold;
        pending.Gold = 0;
        self.Set(new SaveNow());
        ctx.Reply(a.ReplyTo, gold);
    }
}

/// <summary>Writes its changed record every second (checkpoints).</summary>
[Actor<CheckpointedBankId>(SaveEverySeconds = 1)]
public static class CheckpointedBank
{
    [Handle] internal static int On(in Deposit m, ref Ledger ledger, ref Deposits deposits, Entity self) => Banking.Deposit(in m, ref ledger, ref deposits, self);

    [Handle] internal static int On(in Balance _, in Ledger ledger) => ledger.Gold;

    [Handle]
    internal static bool On(in Earn _, Entity self)
    {
        self.GetOrAdd<Earning>();
        return true;
    }
}

/// <summary>Unloads (and writes its record) a second after its last mail.</summary>
[Actor<IdleBankId>(SaveEverySeconds = 0, UnloadAfterIdleSeconds = 1)]
public static class IdleBank
{
    [Handle] internal static int On(in Deposit m, ref Ledger ledger, ref Deposits deposits, Entity self) => Banking.Deposit(in m, ref ledger, ref deposits, self);

    [Handle] internal static int On(in Balance _, in Ledger ledger) => ledger.Gold;
}

/// <summary>Removed without saving after a failed handler (ErrorAction.Reload): its next mail loads it again.</summary>
[Actor<ReloadingBankId>(SaveEverySeconds = 0, OnError = ErrorAction.Reload)]
public static class ReloadingBank
{
    [Handle] internal static int On(in Deposit m, ref Ledger ledger, ref Deposits deposits, Entity self) => Banking.Deposit(in m, ref ledger, ref deposits, self);

    [Handle] internal static int On(in Balance _, in Ledger ledger) => ledger.Gold;

    [Handle]
    internal static Answer<int> On(in Overdraw m, ref Ledger ledger, Entity self, in Context ctx)
    {
        if (m.Async)
        {
            ctx.Ask(ctx.Id<ReloadingBankId>(), new Withdraw(m.Gold), ctx.ReplyLater()); // fails in the answer's handler
            return Answer<int>.Later;
        }

        Banking.Overdraw(m.Gold, ref ledger, self);
        return Answer<int>.Later; // not reached
    }

    /// <summary>Answered with the amount to take: the change is made (and fails) in the answer's handler.</summary>
    [Handle] internal static int On(in Withdraw m) => m.Gold;

    [Handle] internal static void On(in Answered<Withdraw, int> a, ref Ledger ledger, Entity self) => Banking.Overdraw(a.Value, ref ledger, self);
}

/// <summary>The amount an <see cref="Overdraw"/> takes, asked of the bank itself.</summary>
[NinoType, Message] public partial record struct Withdraw(int Gold) : IRequest<int>;

/// <summary>The ledger behavior the bank types share.</summary>
internal static class Banking
{
    public static int Deposit(in Deposit m, ref Ledger ledger, ref Deposits deposits, Entity self)
    {
        int gold = ledger.Gold += m.Gold;
        deposits.Count++;
        if (m.Critical) self.Set(new SaveNow()); // the refs are invalid from here on
        return gold;
    }

    public static int SetGold(in SetGold m, ref Ledger ledger, Entity self)
    {
        ledger.Gold = m.Gold;
        self.Set(new SaveNow());
        return m.Gold;
    }

    /// <summary>A critical change that fails half-way: nothing of it may be written.</summary>
    public static void Overdraw(int gold, ref Ledger ledger, Entity self)
    {
        ledger.Gold -= gold;
        self.Set(new SaveNow());
        throw new InvalidOperationException("overdrawn");
    }
}
