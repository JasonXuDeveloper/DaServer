using DaServer.Clients;
using DaServer.Rpc;
using Nino.Core;

namespace Sample.Contracts;

// Messages shared by servers and clients. A message is a [NinoType, Message] struct, sent between servers; [FromClient]
// lets clients send it and [ToClient] lets servers send it to clients; IRequest<TReply> marks what
// is answered. Plain [NinoType] types (replies, Moved) are data inside messages. Handlers are [Handle] methods; routing,
// dispatch, wire ids, registration and discovery are generated or derived from them.

// ---------------------------------------------------------------- actor ids (one type per actor type)

public readonly record struct PlayerId(long Value) : IActorId;

public readonly record struct MapId(long Value) : IActorId;

/// <summary>The realm: one actor for the whole cluster.</summary>
public readonly record struct RealmId(long Value) : IActorId
{
    public static RealmId The => new(1);
}

// ---------------------------------------------------------------- client to server

/// <summary>Log in (handled by the login service; nothing is bound to the session before).</summary>
[NinoType, Message, FromClient]
public partial record struct Login(string Name) : IRequest<LoginResult>;

[NinoType]
public partial record struct LoginResult(bool Ok, PlayerId Player);

/// <summary>The client's own player (bound to the session at login).</summary>
[NinoType, Message, FromClient]
public partial record struct GetInfo : IRequest<PlayerInfo>;

[NinoType]
public partial record struct PlayerInfo(PlayerId Id, string Name, MapId Map, int Logins);

/// <summary>Move the client's player to a map instance (the map binds the session for input).</summary>
[NinoType, Message, FromClient]
public partial record struct EnterMap(MapId Map) : IRequest<bool>;

/// <summary>Input to the map the session is bound to: a one-way stream.</summary>
[NinoType, Message, FromClient]
public partial record struct Move(float VelocityX, float VelocityY);

[NinoType, Message, FromClient]
public partial record struct Say(string Text);

// ---------------------------------------------------------------- server to server

/// <summary>The login service attaches a client session to its player (the player binds it).</summary>
[NinoType, Message]
public partial record struct Attach(SessionRef Session) : IRequest<bool>;

/// <summary>A player enters a map instance with its client session.</summary>
[NinoType, Message]
public partial record struct Enter(PlayerId Player, SessionRef Session) : IRequest<bool>;

[NinoType, Message]
public partial record struct Leave(SessionRef Session) : IRequest<bool>;

/// <summary>To the realm: who is online.</summary>
[NinoType, Message]
public partial record struct PlayerOnline(PlayerId Player);

[NinoType, Message]
public partial record struct PlayerOffline(PlayerId Player);

// ---------------------------------------------------------------- server to client

/// <summary>
/// An avatar's position and velocity (per tick), sent when its velocity changes or it comes into view: clients
/// extrapolate in between (dead reckoning), so a walking avatar costs nothing until it turns.
/// </summary>
[NinoType]
public partial record struct Moved(PlayerId Player, float X, float Y, float VelocityX, float VelocityY);

/// <summary>Avatars in view that changed direction, or that came into view (one message per map cell).</summary>
[NinoType, Message, ToClient]
public partial record struct Motion(List<Moved> Avatars);

/// <summary>An avatar in view left the map.</summary>
[NinoType, Message, ToClient]
public partial record struct Gone(PlayerId Player);

[NinoType, Message, ToClient]
public partial record struct Chat(PlayerId Player, string Text);

/// <summary>Realm state every client sees (broadcast to every session of the cluster every few ticks).</summary>
[NinoType, Message, ToClient]
public partial record struct RealmStatus(long Tick, int Online, long ServerTimeTicks);
