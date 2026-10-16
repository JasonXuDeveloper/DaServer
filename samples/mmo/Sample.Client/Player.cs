using DaServer.Client;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// A player's flows, as a game client runs them: create its entity for a client, log in, enter a map. Run them on the
/// thread of the player's world (a game's main loop, a swarm's loop): they re-read components after each await.
/// </summary>
public static class Player
{
    /// <summary>The player entity of <paramref name="client"/>; the client's pushes become its data.</summary>
    public static Entity Create(World world, ClusterClient client)
    {
        var player = world.CreateEntity(new Link(client), new Me(), new Position(), new Velocity(), new Steering(),
            new SentSteering(), new View(), new Realm(), new Heard());
        client.Register(new Pushes(world, player));
        return player;
    }

    public static async Task<bool> LogInAsync(Entity player, string name)
    {
        var result = await player.Get<Link>().Client.Ask(new Login(name));
        if (result.Ok) player.Get<Me>().Player = result.Player;
        return result.Ok;
    }

    /// <summary>
    /// Enter <paramref name="map"/>: the bound player moves there and the map binds the session for input. The view
    /// starts empty and takes the new map's pushes at once (they may arrive before the reply, on another path).
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
            return entered = await player.Get<Link>().Client.Ask(new EnterMap(map));
        }
        finally
        {
            if (!entered && player.IsAlive) player.Get<Me>().Map = previous; // still there
        }
    }

    private static void Forget(Entity player)
    {
        var avatars = player.Get<View>().Avatars;
        foreach (var avatar in avatars.Values) avatar.Destroy();
        avatars.Clear();
    }
}
