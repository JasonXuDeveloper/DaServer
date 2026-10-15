using DaServer.Client;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// A player's flows, as a game client runs them: create its entity for a connected client, log in, enter a map. Call
/// them on the player's <see cref="ClientLoop"/> (<see cref="ClientLoop.Run{T}"/>): they touch its components after
/// awaiting, and re-read them, never keeping a ref across an await.
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

    /// <summary>Enter <paramref name="map"/>: the bound player moves there and the map binds the session for input.</summary>
    public static async Task<bool> EnterMapAsync(Entity player, MapId map)
    {
        if (!await player.Get<Link>().Client.Ask(new EnterMap(map))) return false;
        player.Get<Me>().Map = map;
        return true;
    }
}
