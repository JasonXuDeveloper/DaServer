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
// Data (Sample.MapServer.Components): MapGrid on the map entity, GridCell / SentMotion / Despawning / Resynced / SessionGone on
// avatars. Behavior: one system per step, in tick order: DespawnSystem, (movement), PlacementSystem, MotionSyncSystem,
// SnapshotSystem.

/// <summary>Grid operations the interest systems share (the grid itself is data: <see cref="MapGrid"/>).</summary>
internal static class Interest
{
    public static Moved StateOf(Entity avatar)
    {
        var p = avatar.Get<Position>();
        var v = avatar.Get<Velocity>();
        return new Moved(avatar.Get<Avatar>().Player, p.X, p.Y, v.X, v.Y);
    }

    /// <summary>
    /// The session starts viewing the cells around <paramref name="to"/> that it did not see from
    /// <paramref name="from"/> (null: none) and receives the avatars already there.
    /// </summary>
    public static void View(ref MapGrid grid, ICluster cluster, Entity avatar, ClientSession session, int? from, int to)
    {
        var inView = grid.Scratch.Items;
        inView.Clear();
        foreach (int cell in Around(to, stackalloc int[9]))
        {
            if (from is { } f && Near(cell, f)) continue;
            cluster.Send(session, new JoinGroup(grid.Viewers(cell)));
            if (grid.Avatars[cell] is not { } others) continue;
            foreach (var other in others)
                if (other != avatar) inView.Add(StateOf(other));
        }

        if (inView.Count > 0) cluster.Send(session, new Motion(grid.Map, grid.Scratch));
    }

    public static void SwapRemove(List<Entity> list, Entity item)
    {
        int i = list.IndexOf(item);
        if (i < 0) return;
        list[i] = list[^1];
        list.RemoveAt(list.Count - 1);
    }
}

/// <summary>Removes leaving avatars: their viewers forget them, their session stops viewing (unless closed).</summary>
public sealed partial class DespawnSystem : EcsSystem
{
    public override int Priority => -10; // before anything else looks at the avatar this tick

    [ForEach, With<Despawning>]
    private static void Despawn(Entity entity, in Avatar avatar, World world)
    {
        var (player, session) = (avatar.Player, avatar.Session);
        if (entity.TryGet<GridCell>(out var placed) && avatar.Map.IsAlive)
        {
            int cell = placed.Index;
            ref var grid = ref avatar.Map.Get<MapGrid>();
            Interest.SwapRemove(grid.Avatars[cell]!, entity);
            if (grid.Changes[cell]?.Items is { } changes) // a queued change would bring it back after Gone
                for (int i = changes.Count - 1; i >= 0; i--)
                    if (changes[i].Player == player) changes.RemoveAt(i);

            var cluster = world.Cluster;
            cluster.Send(grid.Viewers(cell), new Gone(grid.Map, player));
            if (!entity.Has<SessionGone>())
                foreach (int c in Around(cell, stackalloc int[9]))
                    cluster.Send(session, new LeaveGroup(grid.Viewers(c)));
        }

        entity.Destroy();
    }
}

/// <summary>
/// Keeps avatars in their grid cell: places new ones, moves those that crossed a border (and their sessions between
/// viewer groups), and lets resumed sessions view their 3x3 again.
/// </summary>
public sealed partial class PlacementSystem : EcsSystem
{
    public override int Priority => 10; // after movement

    [ForEach, Without<GridCell>]
    private static void Place(Entity entity, in Position p, in Avatar avatar, World world)
    {
        int to = CellOf(p);
        var session = avatar.Session;
        var map = avatar.Map;
        entity.Add<GridCell>() = new GridCell(to); // `p` and `avatar` are invalid from here on
        ref var grid = ref map.Get<MapGrid>();
        (grid.Avatars[to] ??= new()).Add(entity);
        Interest.View(ref grid, world.Cluster, entity, session, from: null, to);
    }

    [ForEach]
    private static void Cross(Entity entity, in Position p, ref GridCell cell, in Avatar avatar, World world)
    {
        int from = cell.Index, to = CellOf(p);
        if (to == from) return;
        cell = new GridCell(to);

        ref var grid = ref avatar.Map.Get<MapGrid>();
        Interest.SwapRemove(grid.Avatars[from]!, entity);
        (grid.Avatars[to] ??= new()).Add(entity);

        var cluster = world.Cluster;
        foreach (int c in Around(from, stackalloc int[9]))
            if (!Near(c, to)) cluster.Send(avatar.Session, new LeaveGroup(grid.Viewers(c)));

        Interest.View(ref grid, cluster, entity, avatar.Session, from, to);
    }

    [ForEach, With<Resynced>]
    private static void Rejoin(Entity entity, in GridCell cell, in Avatar avatar, World world)
    {
        var (map, session, at) = (avatar.Map, avatar.Session, cell.Index);
        entity.Remove<Resynced>();
        Interest.View(ref map.Get<MapGrid>(), world.Cluster, entity, session, from: null, at);
    }
}

/// <summary>
/// Queues the avatars whose motion or cell changed since their viewers last heard into their cell's next update (the
/// viewers of a new cell learn about an avatar that crossed into it).
/// </summary>
public sealed partial class MotionSyncSystem : EcsSystem
{
    public override int Priority => 20;

    [ForEach, Without<SentMotion>]
    private static void First(Entity entity, in GridCell cell, in Velocity v, in Avatar avatar)
    {
        var (map, at) = (avatar.Map, cell.Index);
        entity.Add<SentMotion>() = new SentMotion(v, at);
        Queue(ref map.Get<MapGrid>(), at, entity);
    }

    [ForEach]
    private static void Changed(Entity entity, in GridCell cell, in Velocity v, ref SentMotion sent, in Avatar avatar)
    {
        var now = new SentMotion(v, cell.Index);
        if (now == sent) return;
        sent = now;
        Queue(ref avatar.Map.Get<MapGrid>(), now.Cell, entity);
    }

    private static void Queue(ref MapGrid grid, int cell, Entity avatar)
    {
        var changes = (grid.Changes[cell] ??= new()).Items;
        if (changes.Count == 0) grid.ChangedCells.Add(cell);
        changes.Add(Interest.StateOf(avatar));
    }
}

/// <summary>Every <see cref="MapGrid.SyncEveryTicks"/> ticks, each map sends one message per changed cell.</summary>
public sealed partial class SnapshotSystem : EcsSystem
{
    public override int Priority => 30;
    public override int UpdateInterval => MapGrid.SyncEveryTicks;

    [ForEach]
    private static void Send(ref MapGrid grid, World world)
    {
        var cluster = world.Cluster;
        foreach (int cell in grid.ChangedCells)
        {
            var changes = grid.Changes[cell]!;
            cluster.Send(grid.Viewers(cell), new Motion(grid.Map, changes));
            changes.Items.Clear();
        }

        grid.ChangedCells.Clear();
    }
}
