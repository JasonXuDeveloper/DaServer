using System.Collections.Generic;
using DaServer.Rpc;
using Nino.Core;

namespace Sample.Contracts;

// Messages shared by servers and clients. A message is a [NinoType] struct; [ClientMessage] marks what game clients may
// send; IRequest<TReply> marks what is answered. Handlers are [Handle] methods; routing, dispatch, wire ids,
// registration and discovery are generated or derived from them.

/// <summary>Well-known actor keys.</summary>
public static class Keys
{
    /// <summary>The realm actor (one activation for the whole cluster).</summary>
    public const long Realm = 1;
}

// ---------------------------------------------------------------- client to server

/// <summary>Log in (handled by the login service; nothing is bound to the session before).</summary>
[NinoType, ClientMessage]
public partial record struct Login(string Name) : IRequest<LoginResult>;

[NinoType]
public partial record struct LoginResult(bool Ok, long PlayerId);

/// <summary>The client's own player (bound to the session at login).</summary>
[NinoType, ClientMessage]
public partial record struct GetInfo : IRequest<PlayerInfo>;

[NinoType]
public partial record struct PlayerInfo(long Id, string Name, long MapId, int Logins);

/// <summary>Move the client's player to a map instance (the map binds the session for input).</summary>
[NinoType, ClientMessage]
public partial record struct EnterMap(long MapId) : IRequest<bool>;

/// <summary>Input to the map the session is bound to: a one-way stream.</summary>
[NinoType, ClientMessage]
public partial record struct Move(float VelocityX, float VelocityY);

[NinoType, ClientMessage]
public partial record struct Say(string Text);

// ---------------------------------------------------------------- server to server

/// <summary>The login service attaches a client session to its player (the player binds it).</summary>
[NinoType]
public partial record struct Attach(SessionRef Session) : IRequest<bool>;

/// <summary>A player enters a map instance (keyed by map id) with its client session.</summary>
[NinoType]
public partial record struct Enter(long PlayerId, SessionRef Session) : IRequest<bool>;

[NinoType]
public partial record struct Leave(SessionRef Session) : IRequest<bool>;

/// <summary>To the realm: who is online.</summary>
[NinoType]
public partial record struct PlayerOnline(long PlayerId);

[NinoType]
public partial record struct PlayerOffline(long PlayerId);

// ---------------------------------------------------------------- server to client

/// <summary>
/// An avatar's position and velocity (per tick), sent when its velocity changes or it comes into view: clients
/// extrapolate in between (dead reckoning), so a walking avatar costs nothing until it turns.
/// </summary>
[NinoType]
public partial record struct Moved(long PlayerId, float X, float Y, float VelocityX, float VelocityY);

/// <summary>Avatars in view that changed direction, or that came into view (one message per map cell).</summary>
[NinoType]
public partial record struct Motion(List<Moved> Avatars);

/// <summary>An avatar in view left the map.</summary>
[NinoType]
public partial record struct Gone(long PlayerId);

[NinoType]
public partial record struct Chat(long PlayerId, string Text);

/// <summary>Realm state every client sees (broadcast to every session of the cluster every few ticks).</summary>
[NinoType]
public partial record struct RealmStatus(long Tick, int Online, long ServerTimeTicks);
