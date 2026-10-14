using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using Sample.Contracts;

namespace Sample.Server;

/// <summary>Stateless login. Runs on gateway nodes (next to the client socket, no extra hop for binding).</summary>
[Role("gateway")]
public sealed class Account : Service, IAccount
{
    private const int MaxNameLength = 32;

    public ValueTask<LoginResult> Login(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            return new(new LoginResult(false, 0));

        // A real game would verify a token against an account store here.
        long playerId = (long)(RpcIds.Fnv1a(name) & 0x7FFF_FFFF) + 1;
        Cluster.BindSession(Caller, playerId);
        return new(new LoginResult(true, playerId));
    }
}
