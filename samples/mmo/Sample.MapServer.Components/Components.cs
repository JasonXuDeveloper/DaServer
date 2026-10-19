using DaServer;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;
using static Sample.Shared.MapGeometry;

namespace Sample.MapServer;

// The map server's data: MapState and MapGrid on the map's entity, Avatar and the interest tags on avatars. Behavior
// is in Sample.MapServer (the Map actor and the interest systems), which a reload replaces; this project stays.

/// <summary>Whose avatar it is, the map entity it lives in and the client session that controls it.</summary>
public record struct Avatar(PlayerId Player, EntityId Map, ClientSession Session) : IComponent;

/// <summary>State of a map: its avatars by client session (its interest grid is <see cref="MapGrid"/>).</summary>
public struct MapState() : IComponent
{
    public readonly Dictionary<ClientSession, EntityId> Avatars = new();
}

/// <summary>The grid cell of a placed avatar (an avatar without it enters the grid on the next tick).</summary>
public record struct GridCell(int Index) : IComponent;

/// <summary>The motion the avatar's viewers last heard about (compared every tick: no flag to keep in sync).</summary>
public record struct SentMotion(Velocity Velocity, int Cell) : IComponent;

/// <summary>Tag: the avatar leaves its map.</summary>
public struct Despawning : IComponent;

/// <summary>Tag: the avatar's session closed, so it already left every group (nothing to leave on despawn).</summary>
public struct SessionGone : IComponent;

/// <summary>Tag: the avatar's client resumed on a new session, which must view the avatar's 3x3 again.</summary>
public struct Resynced : IComponent;

/// <summary>A map's cells: who is in each one, and the changes not sent yet (lists reused: no garbage per tick).</summary>
public struct MapGrid(MapId map) : IComponent
{
    /// <summary>Changes go out every other tick (10 Hz): half the messages, clients extrapolate meanwhile.</summary>
    public const int SyncEveryTicks = 2;

    /// <summary>The map this grid belongs to (its viewer groups are per map).</summary>
    public readonly MapId Map = map;

    public readonly List<EntityId>?[] Avatars = new List<EntityId>?[Side * Side];
    public readonly Moves?[] Changes = new Moves?[Side * Side];
    public readonly List<int> ChangedCells = new();
    public readonly Moves Scratch = new();

    /// <summary>The sessions that see <paramref name="cell"/> of this map.</summary>
    public readonly ClientGroup Viewers(int cell) => ClientGroup.Of<MapGrid>(Map.Value * Side * Side + cell);
}
