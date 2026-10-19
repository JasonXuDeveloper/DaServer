using DaServer;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;
using static Sample.Shared.MapGeometry;

namespace Sample.MapServer;

// The map server's data: MapGrid on the map's entity; Avatar, GridCell and the interest tags on avatars, each a child of
// its map (ChildOf: unloading a map destroys its avatars). Who is where is indexed by EasyEcs, not kept in lists: an
// avatar is found by its session (Avatar), the avatars of a cell by their GridCell. Behavior is in Sample.MapServer (the
// Map actor and the interest systems), which a reload replaces; this project stays.

/// <summary>
/// Whose avatar it is and the client session that controls it. Indexed by the session: input finds its avatar in O(1)
/// (<c>world.Index&lt;Avatar, ClientSession&gt;()</c>), so it is written with <c>Set</c>, never through a ref.
/// </summary>
public readonly record struct Avatar(PlayerId Player, ClientSession Session) : IComponent, IIndexed<ClientSession>
{
    ClientSession IIndexed<ClientSession>.Key => Session;
}

/// <summary>
/// The grid cell of a placed avatar (an avatar without it enters the grid on the next tick). Indexed by its value: the
/// avatars of one cell of one map are <c>world.Index&lt;GridCell, GridCell&gt;().Find(new GridCell(map, cell))</c>.
/// </summary>
public readonly record struct GridCell(EntityId Map, int Index) : IComponent, IIndexed<GridCell>
{
    GridCell IIndexed<GridCell>.Key => this;
}

/// <summary>The motion the avatar's viewers last heard about (compared every tick: no flag to keep in sync).</summary>
public record struct SentMotion(Velocity Velocity, int Cell) : IComponent;

/// <summary>Tag: the avatar's motion changed since its viewers last heard; its cell's next snapshot carries it.</summary>
public struct Queued : IComponent;

/// <summary>Tag: the avatar leaves its map.</summary>
public struct Despawning : IComponent;

/// <summary>Tag: the avatar's session closed, so it already left every group (nothing to leave on despawn).</summary>
public struct SessionGone : IComponent;

/// <summary>Tag: the avatar's client resumed on a new session, which must view the avatar's 3x3 again.</summary>
public struct Resynced : IComponent;

/// <summary>
/// A map's interest grid: its id (the cells' viewer groups are per map) and the buffer its pushes are written from
/// (serialized when sent, so one per map serves every cell: no garbage per tick). Who is in a cell is the
/// <see cref="GridCell"/> index.
/// </summary>
public readonly struct MapGrid(MapId map) : IComponent
{
    /// <summary>Changes go out every other tick (10 Hz): half the messages, clients extrapolate meanwhile.</summary>
    public const int SyncEveryTicks = 2;

    public readonly MapId Map = map;

    public readonly Moves Scratch = new();

    /// <summary>The sessions that see <paramref name="cell"/> of this map.</summary>
    public ClientGroup Viewers(int cell) => ClientGroup.Of<MapGrid>(Map.Value * Side * Side + cell);
}
