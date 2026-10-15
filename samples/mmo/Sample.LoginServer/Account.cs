using DaServer;
using DaServer.Rpc;
using DaServer.Runtime;
using Sample.Contracts;

namespace Sample.LoginServer;

/// <summary>
/// Login: verifies the client and attaches its session to its player; it knows nothing about what the player does
/// with it. Stateless, so the login project scales by adding replicas.
/// </summary>
public sealed class Account : Service
{
    private const int MaxNameLength = 32;

    [Handle]
    public async ValueTask<LoginResult> On(Login m)
    {
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > MaxNameLength) return new LoginResult(false, default);

        // A real game verifies a token against an account store here.
        long hash = 17;
        foreach (char c in m.Name) hash = (hash * 31 + c) & 0x7FFF_FFFF;
        var player = new PlayerId(hash + 1); // ids start at 1
        bool attached = await Cluster.Ask(player, new Attach(Caller));
        return new LoginResult(attached, player);
    }
}
