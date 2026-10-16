using DaServer;
using EasyEcs;
using Sample.Contracts;

namespace Sample.GameServer;

// The game server's data, kept when its logic (Player, Realm, RealmBroadcastSystem) is reloaded.

/// <summary>Persistent player state: the component on the actor's entity.</summary>
public record struct PlayerState(string Name, MapId Map, int Logins, ClientSession Session) : IComponent;

/// <summary>Who is online.</summary>
public struct RealmState() : IComponent
{
    public readonly HashSet<PlayerId> Online = new();
}

