using System.Numerics;

namespace DaServer.Benchmark.MapTick;

/// <summary>
/// The usual object-per-thing server: every avatar is an object with a virtual Update, called in order; it tells its
/// viewers the moment its velocity changes (one frame per event and viewer) and, when it crosses into another cell,
/// sends appear and disappear messages to the avatars that start or stop seeing it.
/// </summary>
public sealed class TraditionalMap
{
    private readonly MapScenario _scenario;
    private readonly GameObject[] _objects;
    private long _tick;

    public TraditionalMap(MapScenario scenario)
    {
        _scenario = scenario;
        _objects = new GameObject[scenario.Avatars];
        for (int i = 0; i < scenario.Avatars; i++)
        {
            var avatar = new AvatarObject(i, scenario.Start(i));
            avatar.Cell = scenario.CellOf(avatar.Position);
            scenario.MoveMember(i, -1, avatar.Cell);
            _objects[i] = avatar;
        }
    }

    public void Tick()
    {
        _scenario.WireLength = 0;
        foreach (var o in _objects) o.Update(_scenario, _tick);
        _tick++;
    }
}

public abstract class GameObject
{
    public abstract void Update(MapScenario map, long tick);
}

public sealed class AvatarObject(int id, Vector2 position) : GameObject
{
    public readonly int Id = id;
    public Vector2 Position = position;
    public Vector2 Velocity;
    public int Cell;

    public override void Update(MapScenario map, long tick)
    {
        if (map.Turns(Id, tick, out var velocity))
        {
            Velocity = velocity;
            Broadcast(map);                                       // right away: one frame per viewer
        }

        var p = Position + Velocity * MapScenario.DeltaTime;
        Position = new Vector2(map.Wrap(p.X), map.Wrap(p.Y));
        int cell = map.CellOf(Position);
        if (cell == Cell) return;
        ChangeCell(map, Cell, cell);
        Cell = cell;
    }

    private void Broadcast(MapScenario map)
    {
        int start = map.Begin();
        map.WriteMoved(Id, Position, Velocity);
        var frame = map.End(start);
        Span<int> around = stackalloc int[9];
        int n = map.Around(Cell, around);
        for (int i = 0; i < n; i++)
            foreach (int viewer in map.Members[around[i]])
                if (viewer != Id) map.Send(viewer, frame);
    }

    /// <summary>Who sees whom is symmetric (3x3 around either): the avatars of gained cells see it now, and it them.</summary>
    private void ChangeCell(MapScenario map, int from, int to)
    {
        Span<int> before = stackalloc int[9], after = stackalloc int[9];
        int nb = map.Around(from, before), na = map.Around(to, after);
        map.MoveMember(Id, from, to);
        int appear = map.Begin();
        map.WriteAppear(Id, Position);
        var me = map.End(appear);
        int disappear = map.Begin();
        map.WriteDisappear(Id);
        var gone = map.End(disappear);
        for (int i = 0; i < na; i++)
        {
            if (before[..nb].IndexOf(after[i]) >= 0) continue;
            foreach (int other in map.Members[after[i]])
            {
                if (other == Id) continue;
                map.Send(other, me);
                int them = map.Begin();
                map.WriteAppear(other, default);
                map.Send(Id, map.End(them));
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
                map.Send(Id, map.End(them));
            }
        }
    }
}
