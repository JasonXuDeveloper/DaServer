using EasyEcs;
using Nino.Core;

namespace DaServer.Benchmark;

// The benchmarks' components: kept apart from the actors and systems that use them (DAS013, ADR-067).

public record struct CounterState(int Value) : IComponent;

public record struct GateState(int Entered) : IComponent;

public struct Fetch : IComponent;

public struct Eat : IComponent;

public struct Nap : IComponent;

public struct Idle : IComponent;

/// <summary>Needs change every tick (Replanning) or never (Running).</summary>
public record struct Restless(bool Changing) : IComponent;

/// <summary>A vault's coins: kept in the actor store.</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Coins(long Amount) : IComponent;
