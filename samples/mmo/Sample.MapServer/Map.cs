using DaServer;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.MapServer;

// A map instance owns the spatial simulation of the players in it. Every avatar is an entity of the map's shard world,
// a child of the map's entity (ChildOf): the Map actor only turns messages into data (spawn, despawn, input
// components); systems do the per-tick work, and Interest.cs decides who hears about what (area of interest). Movement
// itself is a shared rule (Sample.Shared: MovementSystem), the same clients predict with. A map that unloads takes its
// avatars with it (destroying a parent destroys its children): nothing to clean up by hand.

// Sticky (ADR-079): a loaded map stays on its node when map servers join, so the players in it never wait for a move.
[Sticky]
[Actor<MapId>]
public static class Map
{
    private const int MaxChatLength = 256;

    [Handle] internal static void On(in Created _, ref MapGrid grid, in Context ctx) => grid = new MapGrid(ctx.Id<MapId>());

    [Handle]
    internal static Answer<bool> On(in Enter m, in Context ctx)
    {
        var (player, session, map) = (m.Player, m.Session, ctx.Entity.Id);
        if (!TryFind(ctx.World, map, session, out _))
        {
            // Spread over the map by player id (a real game reads the saved position).
            ulong h = (ulong)player.Value * 0x9E3779B97F4A7C15UL >> 32;
            var spawn = new Position(h % 1000 / 1000f * MapGeometry.Size, h / 1000 % 1000 / 1000f * MapGeometry.Size);
            ctx.World.CreateEntity(spawn, new Velocity(), new Avatar(player, session), new ChildOf(map));
            // PlacementSystem puts it into the grid on the next tick; MotionSyncSystem tells its viewers.
        }

        // The client's input reaches this map, and it hears the map's Group, once the binding applied: the reply waits
        // for it while the map takes its next message (thousands entering do not queue behind each round trip).
        return ctx.ReplyAfter(ctx.BindSession(session), true);
    }

    [Handle]
    internal static Answer<bool> On(in Leave m, in Context ctx)
    {
        if (TryFind(ctx.World, ctx.Entity.Id, m.Session, out var avatar)) avatar.SetDespawning();
        return ctx.ReplyAfter(ctx.UnbindSession(m.Session), true);
    }

    // Also delivered when the session's gateway crashed. A closed session already left its groups.
    [Handle]
    internal static void On(in SessionClosed e, in Context ctx)
    {
        if (TryFind(ctx.World, ctx.Entity.Id, e.Session, out var avatar)) avatar.Set(new Despawning(), new SessionGone());
    }

    [Handle]
    internal static void On(in SessionResumed e, in Context ctx)
    {
        // The client resumed: same avatar, new session. It lost the pushes meanwhile, so it gets its view again.
        if (!TryFind(ctx.World, ctx.Entity.Id, e.Previous, out var avatar)) return;
        avatar.Set(avatar.Read<Avatar>() with { Session = e.Session }); // Set: the session index follows
        if (avatar.Has<GridCell>()) avatar.SetResynced(); // not placed yet: placing it shows it the map anyway
    }

    [Handle]
    internal static void On(in Move m, in Context ctx)
    {
        // Input is data: MovementSystem integrates it, MotionSyncSystem tells the avatar's viewers.
        if (TryFind(ctx.World, ctx.Entity.Id, ctx.Caller, out var avatar))
            avatar.Get<Velocity>() = MapGeometry.Clamp(m.VelocityX, m.VelocityY);
    }

    [Handle]
    internal static void On(in Say m, in Context ctx)
    {
        if (m.Text.Length <= MaxChatLength && TryFind(ctx.World, ctx.Entity.Id, ctx.Caller, out var avatar))
            ctx.Send(ctx.Group, new Chat(avatar.Read<Avatar>().Player, m.Text));
    }

    /// <summary>
    /// The avatar <paramref name="session"/> controls in <paramref name="map"/>, through the session index (O(1)). A
    /// session has at most one avatar per map that is not leaving; one it left a moment ago (despawned on the next tick),
    /// or one in another map of this shard, does not count.
    /// </summary>
    private static bool TryFind(World world, EntityId map, ClientSession session, out Entity avatar)
    {
        foreach (var e in world.Index<Avatar, ClientSession>().Find(session))
        {
            if (e.Read<ChildOf>().Parent != map || e.Has<Despawning>()) continue;
            avatar = e;
            return true;
        }

        avatar = default;
        return false;
    }
}
