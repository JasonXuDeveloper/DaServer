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

/// <summary>Whose avatar it is and which client group sees it.</summary>
public record struct Avatar(long PlayerId, ClientGroup Map) : IComponent;

/// <summary>Last position sent to clients (change detection by comparison: no tag churn).</summary>
public record struct NetSync(float X, float Y) : IComponent;

/// <summary>State of a map activation: its avatars by client session.</summary>
public record struct MapState(Dictionary<SessionRef, Entity> Avatars) : IComponent;

public sealed class Map : Actor<MapState>, IMap
{
    private const float MaxSpeed = 5f;
    private const int MaxChatLength = 256;

    public override ValueTask OnActivateAsync()
    {
        State = new MapState(new Dictionary<SessionRef, Entity>());
        return default;
    }

    public ValueTask Enter(long playerId, SessionRef session)
    {
        if (!State.Avatars.ContainsKey(session))
            State.Avatars[session] = World.CreateEntity(new Position(), new Velocity(), new Avatar(playerId, Group),
                new NetSync(float.NaN, float.NaN)); // NaN never equals a position: the first tick syncs it

        Cluster.Bind<IMap>(session, Key); // the client's input now reaches this map
        Cluster.JoinGroup(session, Group);     // and it sees this map's broadcasts
        return default;
    }

    public ValueTask Leave(SessionRef session)
    {
        Cluster.Unbind<IMap>(session);
        Cluster.LeaveGroup(session, Group);
        Remove(session);
        return default;
    }

    [Handle]
    public ValueTask On(SessionClosed e)
    {
        Remove(e.Session); // the gateway already dropped the session's bindings and groups
        return default;
    }

    public void Move(float vx, float vy)
    {
        // Input is data: MovementSystem integrates it on the next tick.
        if (State.Avatars.TryGetValue(Caller, out var avatar))
            avatar.Get<Velocity>() = new Velocity(Math.Clamp(vx, -MaxSpeed, MaxSpeed), Math.Clamp(vy, -MaxSpeed, MaxSpeed));
    }

    public void Chat(string text)
    {
        if (text.Length <= MaxChatLength && State.Avatars.TryGetValue(Caller, out var avatar))
            Cluster.PlayerClient(Group).OnChat(avatar.Get<Avatar>().PlayerId, text);
    }

    public override ValueTask OnDeactivateAsync()
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

/// <summary>
/// Broadcasts positions that changed since the last sync to the avatar's map group (serialized once, one frame per
/// gateway, fanned out there).
/// </summary>
public sealed partial class PositionSyncSystem : EcsSystem
{
    public override int Priority => 10; // after movement

    [ForEach]
    private static void Sync(in Position p, ref NetSync sent, in Avatar avatar, World world)
    {
        if (p.X == sent.X && p.Y == sent.Y) return;
        sent = new NetSync(p.X, p.Y);
        world.Cluster().PlayerClient(avatar.Map).OnMoved(avatar.PlayerId, p.X, p.Y);
    }
}
