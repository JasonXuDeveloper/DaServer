using EasyEcs;

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
