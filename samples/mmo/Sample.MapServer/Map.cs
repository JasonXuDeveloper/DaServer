using DaServer;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.MapServer;

// A map instance owns the spatial simulation of the players in it. Every avatar is an entity of the map's shard world:
// the Map actor only turns messages into data (spawn, despawn, input components); systems do the per-tick work, and
// Interest.cs decides who hears about what (area of interest). Movement itself is a shared rule (Sample.Shared:
// MovementSystem), the same clients predict with.

/// <summary>Whose avatar it is, the map entity it lives in and the client session that controls it.</summary>
public record struct Avatar(PlayerId Player, Entity Map, ClientSession Session) : IComponent;

/// <summary>State of a map: its avatars by client session (its interest grid is <see cref="MapGrid"/>).</summary>
public struct MapState() : IComponent
{
    public readonly Dictionary<ClientSession, Entity> Avatars = new();
}

public sealed class Map : Actor<MapId, MapState>
{
    private const int MaxChatLength = 256;

    public override ValueTask OnLoad()
    {
        Entity.Add<MapGrid>() = new MapGrid(Id);
        return default;
    }

    [Handle]
    public async ValueTask<bool> On(Enter m)
    {
        var (player, session) = (m.Player, m.Session);
        if (!State.Avatars.ContainsKey(session))
        {
            // Spread over the map by player id (a real game reads the saved position).
            ulong h = (ulong)player.Value * 0x9E3779B97F4A7C15UL >> 32;
            var spawn = new Position(h % 1000 / 1000f * MapGeometry.Size, h / 1000 % 1000 / 1000f * MapGeometry.Size);
            State.Avatars[session] = World.CreateEntity(spawn, new Velocity(), new Avatar(player, Entity, session));
            // PlacementSystem puts it into the grid on the next tick; MotionSyncSystem tells its viewers.
        }

        await BindSession(session); // the client's input reaches this map and it sees the map's Group broadcasts
        return true;
    }

    [Handle]
    public async ValueTask<bool> On(Leave m)
    {
        await UnbindSession(m.Session);
        Despawn(m.Session);
        return true;
    }

    // Also delivered when the session's gateway crashed. A closed session already left its groups.
    [Handle]
    public void On(SessionClosed e)
    {
        if (Despawn(e.Session) is { } avatar) avatar.Add<SessionGone>();
    }

    [Handle]
    public void On(SessionResync e)
    {
        // The client reconnected: same avatar, new session. It lost the pushes meanwhile, so it gets its view again.
        if (!State.Avatars.Remove(e.Previous, out var avatar)) return;
        State.Avatars[e.Session] = avatar;
        avatar.Get<Avatar>().Session = e.Session;
        if (avatar.Has<GridCell>()) avatar.Add<Resynced>(); // not placed yet: placing it shows it the map anyway
    }

    [Handle]
    public void On(Move m)
    {
        // Input is data: MovementSystem integrates it, MotionSyncSystem tells the avatar's viewers.
        if (!State.Avatars.TryGetValue(Caller, out var avatar)) return;
        avatar.Get<Velocity>() = MapGeometry.Clamp(m.VelocityX, m.VelocityY);
    }

    [Handle]
    public void On(Say m)
    {
        if (m.Text.Length <= MaxChatLength && State.Avatars.TryGetValue(Caller, out var avatar))
            Cluster.Send(Group, new Chat(avatar.Get<Avatar>().Player, m.Text));
    }

    public override ValueTask OnUnload()
    {
        foreach (var avatar in State.Avatars.Values)
        {
            avatar.Add<Despawning>();
            avatar.Add<SessionGone>(); // viewer groups are per map: they stay with it wherever it loads next
        }

        State.Avatars.Clear();
        return default;
    }

    /// <summary>The avatar leaves the map: <see cref="DespawnSystem"/> removes it on the next tick.</summary>
    private Entity? Despawn(ClientSession session)
    {
        if (!State.Avatars.Remove(session, out var avatar)) return null;
        avatar.Add<Despawning>();
        return avatar;
    }
}
