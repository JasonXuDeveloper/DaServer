using System.Numerics;
using EasyEcs;

namespace DaServer.Benchmark.MapTick;

// The components of MapTickBenchmark's ECS map: an avatar's data, one aspect per component.

public record struct AvatarId(int Value) : IComponent;

public record struct Position(Vector2 Value) : IComponent;

/// <summary>Tracked: a change is what viewers are told (dead reckoning).</summary>
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;

/// <summary>Tracked: a change of cell is a change of who sees the avatar.</summary>
public record struct Cell(int Index) : IComponent, ITrackChanges;

/// <summary>The cell whose 3x3 neighbourhood the avatar watched last tick.</summary>
public record struct Watching(int Cell) : IComponent;

// The map's singletons: the scenario and clock, and each cell's outbox.

public record struct MapTickClock(MapScenario Scenario, long Tick) : ISingletonComponent;

/// <summary>A velocity change waiting in its cell's outbox.</summary>
public readonly record struct Change(int Avatar, Vector2 At, Vector2 Velocity);

/// <summary>Each cell's changes this tick, and the cells that have some (sent and cleared by FlushSystem).</summary>
public readonly record struct Outboxes(int[] Pending, List<int> Busy, List<Change>[] Changes) : ISingletonComponent
{
    public void Add(int cell, in Change change)
    {
        if (Pending[cell]++ == 0) Busy.Add(cell);
        Changes[cell].Add(change);
    }
}

