using System.Runtime.InteropServices;
using DaServer;
using Nino.Core;

namespace Sample.Contracts;

// Messages shared by servers and clients. A message is a [NinoType, Message] struct, sent between servers; [FromClient]
// lets clients send it and [ToClient] lets servers send it to clients; IRequest<TReply> marks what
// is answered. Plain [NinoType] types (replies, Moved) are data inside messages. Handlers are [Handle] methods; routing,
// dispatch, type ids, registration and discovery are generated or derived from them.

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

[NinoType, StructLayout(LayoutKind.Sequential, Pack = 1)] // no padding on the wire
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

/// <summary>
/// A chat line. UTF-8: chat is mostly ASCII with an emoji or a name in another script now and then, which the default
/// encoding would write entirely as UTF-16 (names and other ASCII text take the default, one byte a char).
/// </summary>
[NinoType, Message, FromClient]
public partial record struct Say([property: NinoUtf8] string Text);

// ---------------------------------------------------------------- server to server

/// <summary>The login service attaches a client session to its player (the player binds it).</summary>
[NinoType, Message]
public partial record struct Attach(ClientSession Session) : IRequest<bool>;

/// <summary>A player enters a map instance with its client session.</summary>
[NinoType, Message]
public partial record struct Enter(PlayerId Player, ClientSession Session) : IRequest<bool>;

[NinoType, Message]
public partial record struct Leave(ClientSession Session) : IRequest<bool>;

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

/// <summary>
/// Avatars in view that changed direction, or that came into view (one message per map cell). The map tells clients
/// that changed maps which pushes are late ones from the map they left.
/// </summary>
[NinoType, Message, ToClient]
public partial record struct Motion(MapId Map, Moves Avatars);

/// <summary>
/// The avatars of a <see cref="Motion"/>. A receiver gets one from Nino's pool and gives it back with
/// <c>ReturnToPool()</c> when its handler is done, so the bulk of the push traffic allocates nothing (its list stays
/// attached and is refilled); a sender owns its own (a map's grid keeps one per cell).
/// </summary>
[NinoType, NinoPooled]
public sealed partial class Moves
{
    public List<Moved> Items = new();

    private static partial IEnumerable<Moves> OnNinoPoolWarmup()
    {
        for (int i = 0; i < 64; i++) yield return new Moves();
    }

    private partial void OnNinoPoolReturn() { }
}

/// <summary>An avatar in view left the map.</summary>
[NinoType, Message, ToClient]
public partial record struct Gone(MapId Map, PlayerId Player);

[NinoType, Message, ToClient]
public partial record struct Chat(PlayerId Player, [property: NinoUtf8] string Text);

/// <summary>Realm state every client sees (broadcast to every session of the cluster every <see cref="Timing.RealmStatusEveryTicks"/> ticks).</summary>
[NinoType, Message, ToClient, StructLayout(LayoutKind.Sequential, Pack = 4)] // sent to every session: no tail padding
public partial record struct RealmStatus(long Tick, int Online, long ServerTimeTicks);

/// <summary>The game's clock, the same on servers and clients.</summary>
public static class Timing
{
    /// <summary>Simulation ticks per second (the nodes' default tick rate; clients predict at the same rate).</summary>
    public const int TickRate = 20;

    /// <summary>The realm status goes to every session every this many ticks.</summary>
    public const int RealmStatusEveryTicks = 5;
}
