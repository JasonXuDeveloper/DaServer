namespace DaServer.Tests;

// ---------------------------------------------------------------- slot allocation (ADR-079)

public readonly record struct PinId(long Value) : IActorId;
public readonly record struct LoadId(long Value) : IActorId;

/// <summary>Counts its visits (its state moves with it).</summary>

/// <summary>Stays where it is loaded.</summary>
[Sticky]
public sealed class Pinned : Actor<PinId, Ledger>
{
    [Handle] public int On(Visit _) => ++State.Gold;
}

/// <summary>Evened out by load.</summary>
public sealed class Spread : Actor<LoadId, Ledger>
{
    [Handle] public int On(Visit _) => ++State.Gold;
}
