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
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > MaxNameLength) return new LoginResult(false, 0);

        // A real game verifies a token against an account store here.
        long playerId = 17;
        foreach (char c in m.Name) playerId = (playerId * 31 + c) & 0x7FFF_FFFF;
        playerId++; // ids start at 1
        bool attached = await Cluster.Ask(playerId, new Attach(CallerSession));
        return new LoginResult(attached, playerId);
    }
}
