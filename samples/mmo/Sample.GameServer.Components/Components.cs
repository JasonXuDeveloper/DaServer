using DaServer;
using EasyEcs;
using Nino.Core;
using Sample.Contracts;

namespace Sample.GameServer;

// The game server's data, kept when its logic (Player, Realm, RealmBroadcastSystem) is reloaded.

/// <summary>The player's profile: kept in the actor store (saved on unload and every 30 s while it changed).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct PlayerProfile(string Name, int Logins) : IComponent;

/// <summary>Where the player is now: rebuilt by the next login, never stored.</summary>
public record struct PlayerState(MapId Map, ClientSession Session) : IComponent;

/// <summary>Who is online.</summary>
public struct RealmState() : IComponent
{
    public readonly HashSet<PlayerId> Online = new();
}

