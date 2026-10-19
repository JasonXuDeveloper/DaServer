using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// The tests' components: kept apart from the actors and systems that use them (DAS013, ADR-067).

public record struct CounterState(int Value) : IComponent;

/// <summary>Tag: the counter grows by one every tick (written by the actor, processed by GrowthSystem).</summary>
public struct Growing : IComponent;

public record struct Visits(int Count) : IComponent;

public record struct FragileState(int Value) : IComponent;

public record struct Wallet(int Gold) : IComponent;

public struct Fetch : IComponent;

public struct Eat : IComponent;

public struct Nap : IComponent;

public struct Idle : IComponent;

/// <summary>A bank account's balance: persisted (ADR-068).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Ledger(int Gold) : IComponent;

/// <summary>Tag: the ledger earns one gold every tick (InterestSystem), so its record changes all the time.</summary>
public struct Earning : IComponent;

/// <summary>Deposits so far: persisted beside the ledger (a record has a section per component).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Deposits(int Count) : IComponent;

/// <summary>A link of a chain: a record holding one that points back at itself does not serialize (Nino refuses cycles).</summary>
[NinoType] public sealed partial class Knot { public Knot? Next; }

/// <summary>A persisted component whose record can be made unwritable (<see cref="Knot"/>).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Knotted(Knot? Knot) : IComponent;

/// <summary>
/// A state whose initializer throws while <see cref="Breaks"/> holds 1 (user code in a state's initializers): components
/// are added with their default value, so it runs only where a handler constructs one (<c>Brittle</c>'s <c>Created</c>).
/// </summary>
public record struct Brittleness() : IComponent
{
    public static readonly System.Runtime.CompilerServices.StrongBox<int> Breaks = new();

    public int Value = Breaks.Value == 1 ? throw new InvalidOperationException("a state initializer threw") : 0;
}

/// <summary>Hits in a row: moves with its bank to a new node, but is not stored (ADR-072).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Handoff] public partial record struct Streak(int Count) : IComponent;

/// <summary>
/// A persisted mark whose record does not read on the threads of the node <see cref="RefusedBy"/> names (its setter is
/// user code that runs as the record is read; shard threads are named after their node): a node that cannot take a
/// moved record refuses it.
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist]
public partial record struct Seal : IComponent
{
    public static readonly System.Runtime.CompilerServices.StrongBox<string?> RefusedBy = new();

    private int _mark;

    public int Mark
    {
        readonly get => _mark;
        set => _mark = RefusedBy.Value is { } node && Thread.CurrentThread.Name?.StartsWith(node + "-shard-", StringComparison.Ordinal) == true
            ? throw new InvalidOperationException("a record that does not read")
            : value;
    }
}

/// <summary>
/// A driver's run of asks (allocation and tracing tests): rounds of <see cref="Width"/> asks at once, the next round once
/// the last answer of this one is in; the request it answers at the end (<see cref="Pending"/>, <see cref="Call"/>: a
/// reply token kept as data) and the process's allocated bytes when it started.
/// </summary>
public record struct DriverRun(EntityId Pending, uint Call, long StartBytes, int Kind, long Callee, long Target, int Width, int Rounds, int InFlight)
    : IComponent;

/// <summary>A spoil waiting for its ask's answer: applied (and failed, when <see cref="Fail"/>) then.</summary>
public record struct Spoiling(int Delta, bool Fail) : IComponent;

/// <summary>The second half of a deposit in two steps, made once the other bank answered.</summary>
public record struct PendingDeposit(int Gold) : IComponent;

/// <summary>A request held until the test releases it (a reply token kept as data).</summary>
public record struct HeldReply(EntityId Pending, uint Call) : IComponent;
