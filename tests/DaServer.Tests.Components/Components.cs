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

/// <summary>A state whose initializer throws while <see cref="Breaks"/> holds 1 (user code in a state's initializers).</summary>
public record struct Brittleness() : IComponent
{
    public static readonly System.Runtime.CompilerServices.StrongBox<int> Breaks = new();

    public int Value = Breaks.Value == 1 ? throw new InvalidOperationException("a state initializer threw") : 0;
}

/// <summary>Hits in a row: moves with its bank to a new node, but is not stored (ADR-072).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Handoff] public partial record struct Streak(int Count) : IComponent;
