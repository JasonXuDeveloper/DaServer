using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using Nino.Core;

namespace Sample.Contracts;

// Contracts shared by servers and clients. [Service] / [Actor] / [Push] say how a target is chosen, [Client] what game
// clients may call; proxies, dispatch, wire ids, registration and discovery are generated.

[NinoType]
public partial record struct LoginResult(bool Ok, long PlayerId);

[NinoType]
public partial record struct PlayerInfo(long Id, string Name, long MapId, int Logins);

/// <summary>One avatar's new position (an entry of a map's per-tick snapshot).</summary>
[NinoType]
public partial record struct Moved(long PlayerId, float X, float Y);

/// <summary>Realm state every client sees (broadcast to every session of the cluster every few ticks).</summary>
[NinoType]
public partial record struct RealmStatus(long Tick, int Online, long ServerTimeTicks);

/// <summary>Login (clients call it before anything is bound to their session).</summary>
[Service, Client]
public interface IAccount
{
    ValueTask<LoginResult> Login(string name);
}

/// <summary>A player logged in on a gateway (published by the account service; whoever cares handles it).</summary>
[NinoType]
public readonly partial record struct PlayerLoggedIn([property: Key] long Player, SessionRef Session);

/// <summary>The client's own player (keyed by player id, bound to the session at login).</summary>
[Actor, Client]
public interface IPlayer
{
    ValueTask<PlayerInfo> GetInfo();
    ValueTask EnterMap(long mapId);
}

/// <summary>
/// A map instance (keyed by map id). Servers move players in and out; clients send input to the map their session is
/// bound to (by the map, on enter) as one-way calls: an input stream.
/// </summary>
[Actor]
public interface IMap
{
    ValueTask Enter(long playerId, SessionRef session);
    ValueTask Leave(SessionRef session);
    [Client] void Move(float vx, float vy);
    [Client] void Chat(string text);
}

/// <summary>The realm (one activation, key <see cref="Id"/>): who is online, realm-wide announcements.</summary>
[Actor]
public interface IRealm
{
    const long Id = 1;

    void PlayerOnline(long playerId);
    void PlayerOffline(long playerId);
}

/// <summary>Server-to-client pushes.</summary>
[Push]
public interface IPlayerClient
{
    /// <summary>Positions that changed in the client's map this tick (one message per map per tick).</summary>
    void OnMoves(List<Moved> moves);

    void OnChat(long playerId, string text);

    void OnRealm(RealmStatus status);
}
