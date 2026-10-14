using System.Threading.Tasks;
using DaServer.Rpc;
using Nino.Core;

namespace Sample.Contracts;

// Contracts shared by the server and the game client. Everything else (proxies, dispatch, ids) is generated.

[NinoType]
public partial record struct LoginResult(bool Ok, long PlayerId);

[NinoType]
public partial record struct PlayerInfo(long Id, string Name, long MapId, int Gold);

/// <summary>Stateless login: validates the account and binds the session to the player id.</summary>
[ClientAccessible]
public interface IAccount : IService
{
    ValueTask<LoginResult> Login(string name);
}

/// <summary>The player's persistent state (inventory, currencies, current map). Keyed by player id.</summary>
[ClientAccessible]
public interface IPlayer : IActor
{
    ValueTask<PlayerInfo> GetInfo();
    ValueTask EnterMap(long mapId);
    void Move(float dx, float dy);       // one-way: input stream
    void Chat(string text);              // one-way
}

/// <summary>A map instance: owns the spatial simulation of its players (ECS entities). Keyed by map id.</summary>
public interface IMap : IActor
{
    ValueTask Enter(long playerId, SessionRef session);
    ValueTask Leave(long playerId);
    void SetVelocity(long playerId, float vx, float vy);
}

/// <summary>Server-to-client pushes.</summary>
public interface IGameClient : IClientCallback
{
    void OnMoved(long playerId, float x, float y);
    void OnChat(long playerId, string text);
}
