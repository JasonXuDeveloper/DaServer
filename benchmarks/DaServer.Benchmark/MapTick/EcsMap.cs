using System.Numerics;
using System.Runtime.InteropServices;
using EasyEcs;

namespace DaServer.Benchmark.MapTick;

/// <summary>
/// The same map as a pipeline of systems: turn, integrate (a batch method, SIMD), regroup by cell, update who sees
/// whom for the avatars that changed cell, queue the velocity changes in their cell's outbox, then send each busy
/// cell's outbox as one frame to the avatars around it. Everything sent goes out after the area of interest is up to
/// date.
/// </summary>
public sealed class EcsMap : IDisposable
{
    private readonly World _world = new();

    public EcsMap(MapScenario scenario)
    {
        _world.AddSingleton<MapTickClock>() = new MapTickClock(scenario, 0);
        _world.AddSingleton<Outboxes>() = new Outboxes(new int[scenario.Cells], new List<int>(scenario.Cells), new List<Change>[scenario.Cells]);
        for (int c = 0; c < scenario.Cells; c++) _world.GetSingleton<Outboxes>().Changes[c] = new List<Change>(16);
        _world.AddSystem<TurnSystem>();
        _world.AddSystem<IntegrateSystem>();
        _world.AddSystem<CellSystem>();
        _world.AddSystem<AoiSystem>();
        _world.AddSystem<NearbySystem>();
        _world.AddSystem<FlushSystem>();
        _world.EnsureCapacity<AvatarId, Position, Velocity, Cell, Watching>(scenario.Avatars);
        for (int i = 0; i < scenario.Avatars; i++)
        {
            var at = scenario.Start(i);
            int cell = scenario.CellOf(at);
            scenario.MoveMember(i, -1, cell);
            _world.CreateEntity(new AvatarId(i), new Position(at), new Velocity(), new Cell(cell), new Watching(cell));
        }

        // The first pass: Changed<T> sees every new row once, so this pass tells every viewer every avatar. That is
        // the map's start, not a tick: its frames are not counted.
        _world.Update();
        _world.GetSingleton<MapTickClock>().Tick = 0;
        Array.Clear(scenario.Sessions);
    }

    public void Tick()
    {
        ref var clock = ref _world.GetSingleton<MapTickClock>();
        clock.Scenario.WireLength = 0;
        _world.Update();
        clock.Tick++;
    }

    public void Dispose() => _world.Dispose();
}

public sealed class TurnSystem : EcsSystem
{
    [ForEach]
    private static void Turn(Entity avatar, in AvatarId id, in MapTickClock clock)
    {
        if (clock.Scenario.Turns(id.Value, clock.Tick, out var velocity)) avatar.WriteVelocity().Value = velocity;
    }
}

[UpdateAfter<TurnSystem>]
public sealed class IntegrateSystem : EcsSystem
{
    [ForEach]
    private static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in MapTickClock clock)
    {
        var p = MemoryMarshal.Cast<Position, float>(positions);
        var v = MemoryMarshal.Cast<Velocity, float>(velocities);
        var dt = new Vector<float>(MapScenario.DeltaTime);
        var size = new Vector<float>(clock.Scenario.Size);
        var zero = Vector<float>.Zero;
        int i = 0;
        for (; i <= p.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            var next = new Vector<float>(p[i..]) + new Vector<float>(v[i..]) * dt;
            next += Vector.ConditionalSelect(Vector.LessThan(next, zero), size, zero);              // wrap at the edges
            next -= Vector.ConditionalSelect(Vector.GreaterThanOrEqual(next, size), size, zero);
            next.CopyTo(p[i..]);
        }

        for (; i < p.Length; i++) p[i] = clock.Scenario.Wrap(p[i] + v[i] * MapScenario.DeltaTime);
    }
}

[UpdateAfter<IntegrateSystem>]
public sealed class CellSystem : EcsSystem
{
    [ForEach]
    private static void Regroup(Entity avatar, in AvatarId id, in Position p, in Cell cell, in MapTickClock clock)
    {
        int now = clock.Scenario.CellOf(p.Value);
        if (now == cell.Index) return;
        clock.Scenario.MoveMember(id.Value, cell.Index, now);
        avatar.WriteCell().Index = now;
    }
}

/// <summary>Only the avatars that changed cell: appear and disappear for the avatars of gained and lost cells.</summary>
[UpdateAfter<CellSystem>]
public sealed class AoiSystem : EcsSystem
{
    [ForEach, Changed<Cell>]
    private static void Rewatch(in AvatarId id, in Position p, in Cell cell, ref Watching watching, in MapTickClock clock)
    {
        var map = clock.Scenario;
        Span<int> before = stackalloc int[9], after = stackalloc int[9];
        int nb = map.Around(watching.Cell, before), na = map.Around(cell.Index, after);
        int appear = map.Begin();
        map.WriteAppear(id.Value, p.Value);
        var me = map.End(appear);
        int disappear = map.Begin();
        map.WriteDisappear(id.Value);
        var gone = map.End(disappear);
        for (int i = 0; i < na; i++)
        {
            if (before[..nb].IndexOf(after[i]) >= 0) continue;
            foreach (int other in map.Members[after[i]])
            {
                if (other == id.Value) continue;
                map.Send(other, me);
                int them = map.Begin();
                map.WriteAppear(other, default);
                map.Send(id.Value, map.End(them));
            }
        }

        for (int i = 0; i < nb; i++)
        {
            if (after[..na].IndexOf(before[i]) >= 0) continue;
            foreach (int other in map.Members[before[i]])
            {
                map.Send(other, gone);
                int them = map.Begin();
                map.WriteDisappear(other);
                map.Send(id.Value, map.End(them));
            }
        }

        watching.Cell = cell.Index;
    }
}

/// <summary>Velocity changes of this tick go into their cell's outbox (after the area of interest is up to date).</summary>
[UpdateAfter<AoiSystem>]
public sealed class NearbySystem : EcsSystem
{
    [ForEach, Changed<Velocity>]
    private static void Queue(in AvatarId id, in Position p, in Velocity v, in Cell cell, in Outboxes outboxes) =>
        outboxes.Add(cell.Index, new Change(id.Value, p.Value, v.Value));
}

/// <summary>Each busy cell: one frame with all its changes, sent once to every avatar around it.</summary>
[UpdateAfter<NearbySystem>]
public sealed class FlushSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        var map = World.GetSingleton<MapTickClock>().Scenario;
        var outboxes = World.GetSingleton<Outboxes>();
        Span<int> around = stackalloc int[9];
        foreach (int cell in outboxes.Busy)
        {
            var changes = outboxes.Changes[cell];
            int start = map.Begin();
            foreach (var c in changes) map.WriteMoved(c.Avatar, c.At, c.Velocity);
            var frame = map.End(start);
            int n = map.Around(cell, around);
            for (int i = 0; i < n; i++)
                foreach (int viewer in map.Members[around[i]])
                    map.Send(viewer, frame);
            changes.Clear();
            outboxes.Pending[cell] = 0;
        }

        outboxes.Busy.Clear();
    }
}
