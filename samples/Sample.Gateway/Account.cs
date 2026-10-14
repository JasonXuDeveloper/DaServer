using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using Sample.Contracts;

namespace Sample.Gateway;

/// <summary>
/// Login, hosted by the gateway project: it runs next to the client sockets, so binding the session needs no hop.
/// It binds the session to the player and announces the login; it knows nothing about who reacts to it.
/// </summary>
public sealed class Account : Service, IAccount
{
    private const int MaxNameLength = 32;

    public ValueTask<LoginResult> Login(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            return new(new LoginResult(false, 0));

        // A real game verifies a token against an account store here.
        long playerId = (long)(RpcIds.Fnv1a(name) & 0x7FFF_FFFF) + 1;
        var session = Caller;
        Cluster.Bind<IPlayer>(session, playerId);
        Cluster.Publish(new PlayerLoggedIn(playerId, session));
        return new(new LoginResult(true, playerId));
    }
}
