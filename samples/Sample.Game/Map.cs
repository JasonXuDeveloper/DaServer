using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

// A map instance owns the spatial simulation of the players in it. Every avatar is an entity of the map's shard world:
// the Map actor only turns calls into data (spawn, despawn, input components); systems do the per-tick work, and
// Interest.cs decides who hears about what (area of interest).

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;

/// <summary>Whose avatar it is, the map entity it lives in and the client session that controls it.</summary>
public record struct Avatar(long PlayerId, Entity Map, SessionRef Session) : IComponent;

/// <summary>State of a map: its avatars by client session and its interest grid.</summary>
public struct MapState() : IComponent
{
    public readonly Dictionary<SessionRef, Entity> Avatars = new();
    public readonly InterestGrid Grid = new();
}

public sealed class Map : Actor<MapState>, IMap
{
    private const float MaxSpeed = 5f;
    private const int MaxChatLength = 256;

    public ValueTask Enter(long playerId, SessionRef session)
    {
        if (!State.Avatars.ContainsKey(session))
        {
            // Spread over the map by player id (a real game reads the saved position).
            uint h = RpcIds.Fnv1a(playerId.ToString());
            var spawn = new Position(h % 1000 / 1000f * InterestGrid.Size, h / 1000 % 1000 / 1000f * InterestGrid.Size);
            State.Avatars[session] = World.CreateEntity(spawn, new Velocity(), new Avatar(playerId, Entity, session),
                new GridCell(-1), new MotionChanged(true)); // cell -1: InterestSystem places it on the next tick
        }

        Cluster.Bind<IMap>(session, Key); // the client's input reaches this map and it sees the map's Group broadcasts
        return default;
    }

    public ValueTask Leave(SessionRef session)
    {
        Cluster.Unbind<IMap>(session);
        Remove(session, closed: false);
        return default;
    }

    [Handle]
    public ValueTask On(SessionClosed e)
    {
        Remove(e.Session, closed: true); // also delivered when the session's gateway crashed
        return default;
    }

    public void Move(float vx, float vy)
    {
        // Input is data: MovementSystem integrates it, the next sync tells the avatar's viewers.
        if (!State.Avatars.TryGetValue(CallerSession, out var avatar)) return;
        avatar.Get<Velocity>() = new Velocity(Math.Clamp(vx, -MaxSpeed, MaxSpeed), Math.Clamp(vy, -MaxSpeed, MaxSpeed));
        avatar.Get<MotionChanged>() = new MotionChanged(true);
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

    private void Remove(SessionRef session, bool closed)
    {
        if (!State.Avatars.Remove(session, out var avatar)) return;
        State.Grid.Remove(Cluster, Key, avatar, leaveGroups: !closed); // a closed session left its groups already
        avatar.Destroy();
    }
}

/// <summary>Integrates velocities inside the map; an avatar stopped by the border tells its viewers.</summary>
public sealed partial class MovementSystem : EcsSystem
{
    [ForEach]
    private static void Move(ref Position p, ref Velocity v, ref MotionChanged changed)
    {
        if (v.X == 0 && v.Y == 0) return;
        float x = p.X + v.X, y = p.Y + v.Y;
        float cx = Math.Clamp(x, 0f, InterestGrid.Size), cy = Math.Clamp(y, 0f, InterestGrid.Size);
        p = new Position(cx, cy);
        if (cx == x && cy == y) return;
        v = new Velocity(cx == x ? v.X : 0, cy == y ? v.Y : 0); // clients extrapolate: they must learn about the stop
        changed = new MotionChanged(true);
    }
}
