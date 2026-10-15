using DaServer.Client;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// A player's flows, as a game client runs them: create its entity for a connected client, log in, enter a map. Call
/// them on the thread of the player's world (a game's main loop; a simulated user's loop): they touch its components
/// after awaiting, and re-read them, never keeping a ref across an await.
/// </summary>
public static class Player
{
    /// <summary>The player entity of <paramref name="client"/>; the client's pushes become its data (<see cref="PushSystem"/>).</summary>
    public static Entity Create(World world, ClusterClient client)
    {
        var player = world.CreateEntity(new Link(client), new Position(), new Velocity(), new Steering(), new SentSteering(),
            new View(), new Realm(), new Heard());
        client.Register(new Pushes(world, player));
        return player;
    }

    public static async Task<bool> LogInAsync(Entity player, string name)
    {
        var result = await player.Get<Link>().Client.Ask(new Login(name));
        if (!result.Ok) return false;
        player.Add<Me>() = new Me(result.Player, default);
        return true;
    }

    /// <summary>
    /// Enter <paramref name="map"/>: the bound player moves there and the map binds the session for input. The view
    /// starts empty and takes the new map's pushes at once (they may arrive before the reply, on another path); late
    /// ones from the map left are dropped (<see cref="Pushes"/>).
    /// </summary>
    public static async Task<bool> EnterMapAsync(Entity player, MapId map)
    {
        var previous = player.Get<Me>().Map;
        if (previous == map) return true;
        Forget(player);
        player.Get<Me>().Map = map;
        bool entered = false;
        try
        {
            entered = await player.Get<Link>().Client.Ask(new EnterMap(map));
            return entered;
        }
        finally
        {
            if (!entered && player.IsAlive && player.Get<Me>().Map == map) player.Get<Me>().Map = previous; // still there
        }
    }

    /// <summary>Forget every avatar in view.</summary>
    private static void Forget(Entity player)
    {
        var avatars = player.Get<View>().Avatars;
        foreach (var avatar in avatars.Values) avatar.Destroy();
        avatars.Clear();
    }
}
