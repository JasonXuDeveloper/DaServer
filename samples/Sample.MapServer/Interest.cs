using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.MapServer;

// Area of interest: a map is a grid of cells and a player sees the 3x3 cells around its avatar. Every cell has a client
// group (its viewers: the sessions whose 3x3 contains it), so a change is serialized once per cell and fanned out by
// the gateways, and a player hears only about avatars near it. Updates carry velocity and are sent only when it changes
// (dead reckoning), so 10,000 players in one map cost each client the turns of the ~200 avatars around it, instead of
// every position of everyone every tick (10,000 x 10,000 x 20/s).
//
// Data: MapGrid on the map entity, GridCell / SentMotion / Despawning / Resynced on avatars. Behavior: one system per
// step, in tick order: DespawnSystem, (movement), PlacementSystem, MotionSyncSystem, SnapshotSystem.

/// <summary>The grid cell of a placed avatar (an avatar without it enters the grid on the next tick).</summary>
public record struct GridCell(int Index) : IComponent;

/// <summary>The motion the avatar's viewers last heard about (compared every tick: no flag to keep in sync).</summary>
public record struct SentMotion(Velocity Velocity, int Cell) : IComponent;

/// <summary>The avatar leaves its map (left it, or its session closed: then its groups are already gone).</summary>
public record struct Despawning(bool LeaveGroups) : IComponent;

/// <summary>Tag: the avatar's client resumed on a new session, which must view the avatar's 3x3 again.</summary>
public struct Resynced : IComponent;

/// <summary>A map's cells: who is in each one, and the changes not sent yet (lists reused: no garbage per tick).</summary>
public struct MapGrid() : IComponent
{
    public const float Size = 2000f;
    public const float CellSize = 100f;
    public const int Side = (int)(Size / CellSize);

    /// <summary>Changes go out every other tick (10 Hz): half the messages, clients extrapolate meanwhile.</summary>
    public const int SyncEveryTicks = 2;


    public readonly List<Entity>?[] Avatars = new List<Entity>?[Side * Side];
    public readonly List<Moved>?[] Changes = new List<Moved>?[Side * Side];
    public readonly List<int> ChangedCells = new();
    public readonly List<Moved> Scratch = new();

    public static int CellOf(in Position p) =>
        Math.Min((int)(p.Y / CellSize), Side - 1) * Side + Math.Min((int)(p.X / CellSize), Side - 1);

    /// <summary>The sessions that see <paramref name="cell"/> of map <paramref name="map"/>.</summary>
    public static ClientGroup Viewers(long map, int cell) => ClientGroup.Of<MapGrid>(map * Side * Side + cell);

    /// <summary>The cell at (<paramref name="dx"/>, <paramref name="dy"/>) from <paramref name="cell"/>; -1 outside the map.</summary>
    public static int Offset(int cell, int dx, int dy)
    {
        int x = cell % Side + dx, y = cell / Side + dy;
        return x < 0 || y < 0 || x >= Side || y >= Side ? -1 : y * Side + x;
    }

    /// <summary>Whether <paramref name="a"/> is in the 3x3 around <paramref name="b"/>.</summary>
    public static bool Near(int a, int b) => Math.Abs(a % Side - b % Side) <= 1 && Math.Abs(a / Side - b / Side) <= 1;

    public static Moved StateOf(Entity avatar)
    {
        var p = avatar.Get<Position>();
        var v = avatar.Get<Velocity>();
        return new Moved(avatar.Get<Avatar>().PlayerId, p.X, p.Y, v.X, v.Y);
    }

    /// <summary>
    /// The session starts viewing the cells around <paramref name="to"/> that it did not see from
    /// <paramref name="from"/> (null: none) and receives the avatars already there.
    /// </summary>
    public static void View(ref MapGrid grid, ICluster cluster, long map, Entity avatar, SessionRef session, int? from, int to)
    {
        var inView = grid.Scratch;
        inView.Clear();
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int cell = Offset(to, dx, dy);
            if (cell < 0 || (from is { } f && Near(cell, f))) continue;
            cluster.JoinGroup(session, Viewers(map, cell));
            if (grid.Avatars[cell] is not { } others) continue;
            foreach (var other in others)
                if (other != avatar) inView.Add(StateOf(other));
        }

        if (inView.Count > 0) cluster.Push(session, new Motion(inView));
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

    [ForEach]
    private static void Despawn(Entity entity, in Despawning despawning, in Avatar avatar, World world)
    {
        var (player, session, leaveGroups) = (avatar.PlayerId, avatar.Session, despawning.LeaveGroups);
        if (entity.TryGet<GridCell>(out var placed) && avatar.Map.IsAlive)
        {
            int cell = placed.Index;
            ref var grid = ref avatar.Map.Get<MapGrid>();
            MapGrid.SwapRemove(grid.Avatars[cell]!, entity);
            if (grid.Changes[cell] is { } changes) // a queued change would bring it back after Gone
                for (int i = changes.Count - 1; i >= 0; i--)
                    if (changes[i].PlayerId == player) changes.RemoveAt(i);

            var cluster = world.Cluster();
            long map = avatar.Map.Get<ActorRef>().Key;
            cluster.Broadcast(MapGrid.Viewers(map, cell), new Gone(player));
            if (leaveGroups)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (MapGrid.Offset(cell, dx, dy) is var c and >= 0) cluster.LeaveGroup(session, MapGrid.Viewers(map, c));
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
        int to = MapGrid.CellOf(p);
        var session = avatar.Session;
        var map = avatar.Map;
        entity.Add<GridCell>() = new GridCell(to); // `p` and `avatar` are invalid from here on
        ref var grid = ref map.Get<MapGrid>();
        (grid.Avatars[to] ??= new()).Add(entity);
        MapGrid.View(ref grid, world.Cluster(), map.Get<ActorRef>().Key, entity, session, from: null, to);
    }

    [ForEach]
    private static void Cross(Entity entity, in Position p, ref GridCell cell, in Avatar avatar, World world)
    {
        int from = cell.Index, to = MapGrid.CellOf(p);
        if (to == from) return;
        cell = new GridCell(to);

        ref var grid = ref avatar.Map.Get<MapGrid>();
        MapGrid.SwapRemove(grid.Avatars[from]!, entity);
        (grid.Avatars[to] ??= new()).Add(entity);

        var cluster = world.Cluster();
        long map = avatar.Map.Get<ActorRef>().Key;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int c = MapGrid.Offset(from, dx, dy);
            if (c >= 0 && !MapGrid.Near(c, to)) cluster.LeaveGroup(avatar.Session, MapGrid.Viewers(map, c));
        }

        MapGrid.View(ref grid, cluster, map, entity, avatar.Session, from, to);
    }

    [ForEach, With<Resynced>]
    private static void Rejoin(Entity entity, in GridCell cell, in Avatar avatar, World world)
    {
        var (map, session, at) = (avatar.Map, avatar.Session, cell.Index);
        entity.Remove<Resynced>();
        MapGrid.View(ref map.Get<MapGrid>(), world.Cluster(), map.Get<ActorRef>().Key, entity, session, from: null, at);
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
        var changes = grid.Changes[cell] ??= new();
        if (changes.Count == 0) grid.ChangedCells.Add(cell);
        changes.Add(MapGrid.StateOf(avatar));
    }
}

/// <summary>Every <see cref="MapGrid.SyncEveryTicks"/> ticks, each map sends one message per changed cell.</summary>
public sealed partial class SnapshotSystem : EcsSystem
{
    public override int Priority => 30;
    public override int UpdateInterval => MapGrid.SyncEveryTicks;

    [ForEach]
    private static void Send(ref MapGrid grid, in ActorRef actor, World world)
    {
        var cluster = world.Cluster();
        foreach (int cell in grid.ChangedCells)
        {
            var changes = grid.Changes[cell]!;
            cluster.Broadcast(MapGrid.Viewers(actor.Key, cell), new Motion(changes));
            changes.Clear();
        }

        grid.ChangedCells.Clear();
    }
}
