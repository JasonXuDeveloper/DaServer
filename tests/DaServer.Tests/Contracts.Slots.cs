namespace DaServer.Tests;

// ---------------------------------------------------------------- slot allocation (ADR-079)

public readonly record struct PinId(long Value) : IActorId;
public readonly record struct LoadId(long Value) : IActorId;

/// <summary>Stays where it is loaded; counts its visits (its state moves with it).</summary>
[Sticky]
[Actor<PinId>]
public static class Pinned
{
    [Handle] internal static int On(in Visit _, ref Ledger ledger) => ++ledger.Gold;
}

/// <summary>Evened out by load; counts its visits.</summary>
[Actor<LoadId>]
public static class Spread
{
    [Handle] internal static int On(in Visit _, ref Ledger ledger) => ++ledger.Gold;
}
