using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

// A map instance owns the spatial simulation of the players in it. Every avatar is an entity of the map's shard world:
// the Map actor only turns calls into data (spawn, despawn, input components); systems do the per-tick work.

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;

/// <summary>Whose avatar it is and the map entity it lives in.</summary>
public record struct Avatar(long PlayerId, Entity Map) : IComponent;

/// <summary>Last position sent to clients (change detection by comparison: no tag churn).</summary>
public record struct NetSync(float X, float Y) : IComponent;

/// <summary>
/// State of a map: its avatars by client session and the positions that changed this tick (sent as one message per
/// tick, reused: no garbage per tick).
/// </summary>
public struct MapState() : IComponent
{
    public readonly Dictionary<SessionRef, Entity> Avatars = new();
    public readonly List<Moved> Moves = new();
}

public sealed class Map : Actor<MapState>, IMap
{
    private const float MaxSpeed = 5f;
    private const int MaxChatLength = 256;

    public ValueTask Enter(long playerId, SessionRef session)
    {
        if (!State.Avatars.ContainsKey(session))
            State.Avatars[session] = World.CreateEntity(new Position(), new Velocity(), new Avatar(playerId, Entity),
                new NetSync(float.NaN, float.NaN)); // NaN never equals a position: the first tick syncs it

        Cluster.Bind<IMap>(session, Key); // the client's input reaches this map and it sees the map's Group broadcasts
        return default;
    }

    public ValueTask Leave(SessionRef session)
    {
        Cluster.Unbind<IMap>(session);
        Remove(session);
        return default;
    }

    [Handle]
    public ValueTask On(SessionClosed e)
    {
        Remove(e.Session); // also delivered when the session's gateway crashed
        return default;
    }

    public void Move(float vx, float vy)
    {
        // Input is data: MovementSystem integrates it on the next tick.
        if (State.Avatars.TryGetValue(CallerSession, out var avatar))
            avatar.Get<Velocity>() = new Velocity(Math.Clamp(vx, -MaxSpeed, MaxSpeed), Math.Clamp(vy, -MaxSpeed, MaxSpeed));
    }

    public void Chat(string text)
    {
        if (text.Length <= MaxChatLength && State.Avatars.TryGetValue(CallerSession, out var avatar))
            Cluster.PlayerClient(Group).OnChat(avatar.Get<Avatar>().PlayerId, text);
    }

    public override ValueTask OnUnload()
    {
        foreach (var avatar in State.Avatars.Values) avatar.Destroy();
        State.Avatars.Clear();
        return default;
    }

    private void Remove(SessionRef session)
    {
        if (State.Avatars.Remove(session, out var avatar)) avatar.Destroy();
    }
}

/// <summary>Integrates velocities inside the map bounds.</summary>
public sealed partial class MovementSystem : EcsSystem
{
    private const float Size = 1000f;

    [ForEach]
    private static void Move(ref Position p, in Velocity v) =>
        p = new Position(Math.Clamp(p.X + v.X, 0f, Size), Math.Clamp(p.Y + v.Y, 0f, Size));
}

/// <summary>Collects the positions that changed since the last sync into their map's snapshot.</summary>
public sealed partial class PositionSyncSystem : EcsSystem
{
    public override int Priority => 10; // after movement

    [ForEach]
    private static void Sync(in Position p, ref NetSync sent, in Avatar avatar)
    {
        if (p.X == sent.X && p.Y == sent.Y) return;
        sent = new NetSync(p.X, p.Y);
        avatar.Map.Get<MapState>().Moves.Add(new Moved(avatar.PlayerId, p.X, p.Y));
    }
}

/// <summary>
/// Sends each map's snapshot to the map's group: one message per map per tick, serialized once, one frame per gateway,
/// fanned out there (n avatars moving cost n entries, not n x n messages).
/// </summary>
public sealed partial class SnapshotSystem : EcsSystem
{
    public override int Priority => 20; // after the snapshot is complete

    [ForEach]
    private static void Send(ref MapState map, in ActorIdentity actor, World world)
    {
        if (map.Moves.Count == 0) return;
        world.Cluster().PlayerClient(actor.Group).OnMoves(map.Moves);
        map.Moves.Clear();
    }
}
