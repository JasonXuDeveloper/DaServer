using System.Threading.Tasks;
using DaServer.Rpc;
using Nino.Core;

namespace Sample.Contracts;

// Contracts shared by servers and clients. The marker says who may call a contract and how its target is chosen;
// proxies, dispatch, wire ids and registration are generated. A contract is cut per audience: what clients may call
// (IPublicService / ISessionActor) is separate from what servers call (IActor), even when one actor implements both.

[NinoType]
public partial record struct LoginResult(bool Ok, long PlayerId);

[NinoType]
public partial record struct PlayerInfo(long Id, string Name, long MapId, int Logins);

/// <summary>Login (clients call it before anything is bound to their session).</summary>
public interface IAccount : IPublicService
{
    ValueTask<LoginResult> Login(string name);
}

/// <summary>A player logged in on a gateway (published by the account service; whoever cares handles it).</summary>
[NinoType]
public readonly partial record struct PlayerLoggedIn(long Key, SessionRef Session) : IKeyedEvent;

/// <summary>The client's own player (keyed by player id, bound to the session at login).</summary>
public interface IPlayer : ISessionActor
{
    ValueTask<PlayerInfo> GetInfo();
    ValueTask EnterMap(long mapId);
}

/// <summary>A map instance as servers see it: players enter and leave it (keyed by map id).</summary>
public interface IMap : IActor
{
    ValueTask Enter(long playerId, SessionRef session);
    ValueTask Leave(SessionRef session);
}

/// <summary>Client input to the map the session is in (bound by the map on enter). One-way: an input stream.</summary>
public interface IMapInput : ISessionActor
{
    void Move(float vx, float vy);
    void Chat(string text);
}

/// <summary>Server-to-client pushes.</summary>
public interface IPlayerClient : IClientCallback
{
    void OnMoved(long playerId, float x, float y);
    void OnChat(long playerId, string text);
}
