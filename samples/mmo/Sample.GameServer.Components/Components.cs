using DaServer;
using EasyEcs;
using Nino.Core;
using Sample.Contracts;

namespace Sample.GameServer;

// The game server's data, kept when its logic (Player, Realm, RealmBroadcastSystem) is reloaded.

/// <summary>The player's profile: kept in the actor store (saved on unload and every 30 s while it changed).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct PlayerProfile(string Name, int Logins) : IComponent;

/// <summary>
/// Where the player is now and the session it plays from: moves with the player when its node leaves or the cluster
/// grows (a handoff), never stored (the next login rebuilds it).
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant), Handoff] public partial record struct PlayerState(MapId Map, ClientSession Session) : IComponent;

/// <summary>
/// A map change in flight (<c>EnterMap</c>: leave the previous map, then enter this one), between the answers that
/// carry it on. Its absence means none: a second EnterMap meanwhile is refused instead of interleaving with it. Never
/// stored or moved: the caller's reply token does not move either.
/// </summary>
public record struct Entering(MapId Map, ClientSession Session) : IComponent;

/// <summary>Who is online, and the realm's tick count: both move with the realm (a handoff).</summary>
[NinoType(format: NinoFormat.VersionTolerant), Handoff]
public partial struct RealmState() : IComponent
{
    public HashSet<PlayerId> Online = new();

    /// <summary>
    /// The realm's tick count when it last sent its status (<see cref="RealmStatus.Tick"/>): one sequence whichever node
    /// runs it.
    /// </summary>
    public long Ticks;
}

