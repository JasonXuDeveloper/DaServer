using DaServer;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;
using static Sample.Shared.MapGeometry;

namespace Sample.MapServer;

// Area of interest: a map is a grid of cells and a player sees the 3x3 cells around its avatar. Every cell has a client
// group (its viewers: the sessions whose 3x3 contains it), so a change is serialized once per cell and fanned out by
// the gateways, and a player hears only about avatars near it. Updates carry velocity and are sent only when it changes
// (dead reckoning), so 10,000 players in one map cost each client the turns of the ~200 avatars around it, instead of
// every position of everyone every tick (10,000 x 10,000 x 20/s).
//
// Data (Sample.MapServer.Components): MapGrid on the map entity, GridCell (indexed: the avatars of a cell) / SentMotion /
// Queued / Despawning / Resynced / SessionGone on avatars. Behavior: one system per step, in tick order: DespawnSystem,
// (movement), PlacementSystem, MotionSyncSystem, SnapshotSystem.

/// <summary>Grid operations the interest systems share (the grid itself is data: <see cref="MapGrid"/>, <see cref="GridCell"/>).</summary>
internal static class Interest
{
    public static Moved StateOf(Entity avatar)
    {
        var p = avatar.Position;
        var v = avatar.Velocity;
        return new Moved(avatar.Avatar.Player, p.X, p.Y, v.X, v.Y);
    }

    /// <summary>
    /// The session starts viewing the cells around <paramref name="to"/> that it did not see from
    /// <paramref name="from"/> (null: none) and receives the avatars already there.
    /// </summary>
    public static void View(World world, EntityId map, Entity avatar, ClientSession session, int? from, int to)
    {
        var grid = world.GetEntity(map).MapGrid;
        var cells = world.GridCellIndex;
        var cluster = world.Cluster;
        var inView = grid.Scratch.Items;
        inView.Clear();
        foreach (int cell in Around(to, stackalloc int[9]))
        {
            if (from is { } f && Near(cell, f)) continue;
            cluster.Send(session, new JoinGroup(grid.Viewers(cell)));
            foreach (var other in cells.Find(new GridCell(map, cell)))
                if (other.Id != avatar.Id) inView.Add(StateOf(other));
        }

        if (inView.Count > 0) cluster.Send(session, new Motion(grid.Map, grid.Scratch));
    }
}

/// <summary>Removes leaving avatars: their viewers forget them, their session stops viewing (unless closed).</summary>
[UpdateBefore<MovementSystem>]
public sealed partial class DespawnSystem : EcsSystem
{
    [ForEach, With<Despawning>]
    private static void Despawn(Entity entity, in Avatar avatar, in ChildOf map, World world)
    {
        if (entity.TryGetGridCell(out var placed)) // placed: its viewers know it
        {
            var grid = world.GetEntity(map.Parent).MapGrid; // alive: destroying a map destroys its avatars
            var cluster = world.Cluster;
            cluster.Send(grid.Viewers(placed.Index), new Gone(grid.Map, avatar.Player));
            if (!entity.IsSessionGone)
                foreach (int c in Around(placed.Index, stackalloc int[9]))
                    cluster.Send(avatar.Session, new LeaveGroup(grid.Viewers(c)));
        }

        entity.Destroy(); // a queued motion goes with it: no snapshot brings it back after Gone
    }
}

/// <summary>
/// Keeps avatars in their grid cell: places new ones, moves those that crossed a border (and their sessions between
/// viewer groups), and lets resumed sessions view their 3x3 again.
/// </summary>
[UpdateAfter<MovementSystem>]
public sealed partial class PlacementSystem : EcsSystem
{
    [ForEach, Without<GridCell>]
    private static void Place(Entity entity, in Position p, in Avatar avatar, in ChildOf map, World world)
    {
        var (to, session, parent) = (CellOf(p), avatar.Session, map.Parent);
        entity.SetGridCell(parent, to); // `p`, `avatar` and `map` are invalid from here on
        Interest.View(world, parent, entity, session, from: null, to);
    }

    [ForEach]
    private static void Cross(Entity entity, in Position p, in GridCell cell, in Avatar avatar, World world)
    {
        int from = cell.Index, to = CellOf(p);
        if (to == from) return;
        var (session, map) = (avatar.Session, cell.Map);
        entity.SetGridCell(map, to); // indexed: Set keeps the index current (the refs above are not read again)

        var grid = world.GetEntity(map).MapGrid;
        var cluster = world.Cluster;
        foreach (int c in Around(from, stackalloc int[9]))
            if (!Near(c, to)) cluster.Send(session, new LeaveGroup(grid.Viewers(c)));

        Interest.View(world, map, entity, session, from, to);
    }

    [ForEach, With<Resynced>]
    private static void Rejoin(Entity entity, in GridCell cell, in Avatar avatar, World world)
    {
        var (map, session, at) = (cell.Map, avatar.Session, cell.Index);
        entity.RemoveResynced();
        Interest.View(world, map, entity, session, from: null, at);
    }
}

/// <summary>
/// Queues the avatars whose motion or cell changed since their viewers last heard for their cell's next snapshot (the
/// viewers of a new cell learn about an avatar that crossed into it).
/// </summary>
[UpdateAfter<PlacementSystem>]
public sealed partial class MotionSyncSystem : EcsSystem
{
    [ForEach, Without<SentMotion>]
    private static void First(Entity entity, in GridCell cell, in Velocity v)
    {
        var sent = new SentMotion(v, cell.Index);
        entity.Set(sent, new Queued());
    }

    [ForEach]
    private static void Changed(Entity entity, in GridCell cell, in Velocity v, ref SentMotion sent)
    {
        var now = new SentMotion(v, cell.Index);
        if (now == sent) return;
        sent = now;
        entity.SetQueued(); // a tag: once added, adding it again only flips its presence bit
    }
}

/// <summary>
/// Every <see cref="MapGrid.SyncEveryTicks"/> ticks, one message per cell with queued avatars: the first queued avatar
/// of a cell found sends its whole cell (through the <see cref="GridCell"/> index), unqueuing the others.
/// </summary>
[UpdateAfter<MotionSyncSystem>]
public sealed partial class SnapshotSystem : EcsSystem
{
    public override int UpdateInterval => MapGrid.SyncEveryTicks;

    [ForEach, With<Queued>]
    private static void Send(in GridCell cell, Maybe<Queued> queued, World world)
    {
        if (!queued.HasValue) return; // sent with its cell earlier in this pass
        var at = cell;
        var grid = world.GetEntity(at.Map).MapGrid;
        var changes = grid.Scratch.Items;
        changes.Clear();
        foreach (var other in world.GridCellIndex.Find(at))
        {
            if (!other.IsQueued) continue;
            changes.Add(Interest.StateOf(other));
            other.RemoveQueued();
        }

        world.Cluster.Send(grid.Viewers(at.Index), new Motion(grid.Map, grid.Scratch));
    }
}
