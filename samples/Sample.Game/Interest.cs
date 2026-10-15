using System;
using System.Collections.Generic;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

// Area of interest: a map is a grid of cells and a player sees the 3x3 cells around its avatar. Every cell has a client
// group (its viewers: the sessions whose 3x3 contains it), so a change is serialized once per cell and fanned out by
// the gateways, and a player hears only about avatars near it. Updates carry velocity and are sent only when it changes
// (dead reckoning), so 10,000 players in one map cost each client the turns of the ~200 avatars around it, instead of
// every position of everyone every tick (10,000 x 10,000 x 20/s).

/// <summary>The grid cell of an avatar (-1: not placed yet).</summary>
public record struct GridCell(int Index) : IComponent;

/// <summary>The avatar's motion changed since its viewers last heard about it.</summary>
public record struct MotionChanged(bool Value) : IComponent;

/// <summary>A map's cells: who is in each one, and the changes not sent yet (lists reused: no garbage per tick).</summary>
public sealed class InterestGrid
{
    public const float Size = 2000f;
    public const float CellSize = 100f;
    public const int Side = (int)(Size / CellSize);

    /// <summary>Changes go out every other tick (10 Hz): half the messages, clients extrapolate meanwhile.</summary>
    public const int SyncEveryTicks = 2;

    private static readonly uint Scope = RpcIds.Fnv1a("Sample.Game.InterestGrid");

    private readonly List<Entity>?[] _avatars = new List<Entity>?[Side * Side];
    private readonly List<Moved>?[] _changes = new List<Moved>?[Side * Side];
    private readonly List<int> _changedCells = new();
    private readonly List<Moved> _inView = new();
    public long Tick;

    public static int CellOf(in Position p) =>
        Math.Min((int)(p.Y / CellSize), Side - 1) * Side + Math.Min((int)(p.X / CellSize), Side - 1);

    /// <summary>The sessions that see <paramref name="cell"/> of map <paramref name="map"/>.</summary>
    public static ClientGroup Viewers(long map, int cell) => ClientGroup.Of(Scope, map * Side * Side + cell);

    /// <summary>
    /// The avatar moved from cell <paramref name="from"/> (-1: it just entered) to <paramref name="to"/>: its session
    /// stops viewing the cells that left its 3x3, starts viewing the new ones and receives the avatars already there.
    /// </summary>
    public void Move(ICluster cluster, long map, Entity avatar, SessionRef session, int from, int to)
    {
        if (from >= 0) SwapRemove(_avatars[from]!, avatar);
        (_avatars[to] ??= new()).Add(avatar);

        if (from >= 0)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cell = Offset(from, dx, dy);
                if (cell >= 0 && !Near(cell, to)) cluster.LeaveGroup(session, Viewers(map, cell));
            }

        _inView.Clear();
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int cell = Offset(to, dx, dy);
            if (cell < 0 || (from >= 0 && Near(cell, from))) continue;
            cluster.JoinGroup(session, Viewers(map, cell));
            if (_avatars[cell] is not { } others) continue;
            foreach (var other in others)
                if (other != avatar) _inView.Add(StateOf(other));
        }

        if (_inView.Count > 0) cluster.PlayerClient(session).OnMoves(_inView);
    }

    /// <summary>Queue a change of an avatar in <paramref name="cell"/> for the next sync.</summary>
    public void Changed(int cell, in Moved moved)
    {
        var changes = _changes[cell] ??= new();
        if (changes.Count == 0) _changedCells.Add(cell);
        changes.Add(moved);
    }

    /// <summary>One message per changed cell to its viewers.</summary>
    public void Flush(ICluster cluster, long map)
    {
        foreach (int cell in _changedCells)
        {
            var changes = _changes[cell]!;
            cluster.PlayerClient(Viewers(map, cell)).OnMoves(changes);
            changes.Clear();
        }

        _changedCells.Clear();
    }

    /// <summary>The avatar leaves the map: its viewers forget it (and its session stops viewing, unless closed).</summary>
    public void Remove(ICluster cluster, long map, Entity avatar, bool leaveGroups)
    {
        int cell = avatar.Get<GridCell>().Index;
        if (cell < 0) return;
        long player = avatar.Get<Avatar>().PlayerId;
        SwapRemove(_avatars[cell]!, avatar);
        if (_changes[cell] is { } changes) // a queued change would bring it back after OnGone
            for (int i = changes.Count - 1; i >= 0; i--)
                if (changes[i].PlayerId == player) changes.RemoveAt(i);

        cluster.PlayerClient(Viewers(map, cell)).OnGone(player);
        if (!leaveGroups) return;
        var session = avatar.Get<Avatar>().Session;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (Offset(cell, dx, dy) is var c and >= 0) cluster.LeaveGroup(session, Viewers(map, c));
    }

    public static Moved StateOf(Entity avatar)
    {
        var p = avatar.Get<Position>();
        var v = avatar.Get<Velocity>();
        return new Moved(avatar.Get<Avatar>().PlayerId, p.X, p.Y, v.X, v.Y);
    }

    private static int Offset(int cell, int dx, int dy)
    {
        int x = cell % Side + dx, y = cell / Side + dy;
        return x < 0 || y < 0 || x >= Side || y >= Side ? -1 : y * Side + x;
    }

    private static bool Near(int a, int b) => Math.Abs(a % Side - b % Side) <= 1 && Math.Abs(a / Side - b / Side) <= 1;

    private static void SwapRemove(List<Entity> list, Entity item)
    {
        int i = list.IndexOf(item);
        if (i < 0) return;
        list[i] = list[^1];
        list.RemoveAt(list.Count - 1);
    }
}

/// <summary>Moves avatars between cells (and their sessions between viewer groups) when they cross a border.</summary>
public sealed partial class InterestSystem : EcsSystem
{
    public override int Priority => 10; // after movement

    [ForEach]
    private static void Place(Entity entity, in Position p, ref GridCell cell, ref MotionChanged changed, in Avatar avatar,
        World world)
    {
        int to = InterestGrid.CellOf(p);
        if (to == cell.Index) return;
        avatar.Map.Get<MapState>().Grid.Move(world.Cluster(), avatar.Map.Get<ActorIdentity>().Key, entity,
            avatar.Session, cell.Index, to);
        cell = new GridCell(to);
        changed = new MotionChanged(true); // the viewers of the new cells learn about it
    }
}

/// <summary>Queues the avatars whose motion changed into their cell's next update.</summary>
public sealed partial class MotionSyncSystem : EcsSystem
{
    public override int Priority => 20;

    [ForEach]
    private static void Sync(Entity entity, in GridCell cell, ref MotionChanged changed, in Avatar avatar)
    {
        if (!changed.Value || cell.Index < 0) return;
        changed = new MotionChanged(false);
        avatar.Map.Get<MapState>().Grid.Changed(cell.Index, InterestGrid.StateOf(entity));
    }
}

/// <summary>Every <see cref="InterestGrid.SyncEveryTicks"/> ticks, each map sends one message per changed cell.</summary>
public sealed partial class SnapshotSystem : EcsSystem
{
    public override int Priority => 30;

    [ForEach]
    private static void Send(ref MapState map, in ActorIdentity actor, World world)
    {
        if (++map.Grid.Tick % InterestGrid.SyncEveryTicks == 0) map.Grid.Flush(world.Cluster(), actor.Key);
    }
}
