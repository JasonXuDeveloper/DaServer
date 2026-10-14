using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Server;

// A map instance owns the spatial simulation of the players in it. Every avatar is an entity of the map's shard world:
// the Map actor only turns calls into data (spawn, despawn, input); systems do the per-tick work, one job each.

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;

/// <summary>Who an avatar belongs to and where its updates go.</summary>
public record struct Avatar(long PlayerId, SessionRef Session, ClientGroup Map) : IComponent;

/// <summary>Last position sent to clients (change detection by comparison: no tag churn).</summary>
public record struct NetSync(float X, float Y) : IComponent;

/// <summary>State of a map activation: its avatars by player id.</summary>
public record struct MapState(Dictionary<long, Entity> Avatars) : IComponent;

[Role("game")]
public sealed class Map : Actor<MapState>, IMap
{
    private const float MaxSpeed = 5f;

    private ClientGroup Group => new(Key);

    public override ValueTask OnActivateAsync()
    {
        State = new MapState(new Dictionary<long, Entity>());
        return default;
    }

    public ValueTask Enter(long playerId, SessionRef session)
    {
        var avatars = State.Avatars;
        if (!avatars.ContainsKey(playerId))
            avatars[playerId] = World.CreateEntity(new Position(), new Velocity(), new Avatar(playerId, session, Group),
                new NetSync(float.NaN, float.NaN)); // NaN never equals a position: the first tick syncs it

        Cluster.JoinGroup(session, Group); // receive this map's broadcasts
        return default;
    }

    public ValueTask Leave(long playerId)
    {
        if (State.Avatars.Remove(playerId, out var avatar))
        {
            Cluster.LeaveGroup(avatar.Get<Avatar>().Session, Group);
            avatar.Destroy();
        }

        return default;
    }

    public void SetVelocity(long playerId, float vx, float vy)
    {
        // Input only: MovementSystem integrates it on the next tick.
        if (State.Avatars.TryGetValue(playerId, out var avatar))
            avatar.Get<Velocity>() = new Velocity(Math.Clamp(vx, -MaxSpeed, MaxSpeed), Math.Clamp(vy, -MaxSpeed, MaxSpeed));
    }

    public override ValueTask OnDeactivateAsync()
    {
        foreach (var avatar in State.Avatars.Values) avatar.Destroy();
        State.Avatars.Clear();
        return default;
    }
}

/// <summary>Integrates velocities inside the map bounds.</summary>
[Role("game")]
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
[Role("game")]
public sealed partial class PositionSyncSystem : EcsSystem
{
    public override int Priority => 10; // after movement

    [ForEach]
    private static void Sync(in Position p, ref NetSync sent, in Avatar avatar, World world)
    {
        if (p.X == sent.X && p.Y == sent.Y) return;
        sent = new NetSync(p.X, p.Y);
        world.Cluster().GameClient(avatar.Map).OnMoved(avatar.PlayerId, p.X, p.Y);
    }
}
