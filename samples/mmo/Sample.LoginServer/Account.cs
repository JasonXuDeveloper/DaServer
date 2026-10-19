using DaServer;
using DaServer.Runtime;
using Sample.Contracts;

namespace Sample.LoginServer;

/// <summary>
/// Login: verifies the client and attaches its session to its player; it knows nothing about what the player does
/// with it. Stateless, so the login project scales by adding replicas.
/// </summary>
[Service]
public static class Account
{
    private const int MaxNameLength = 32;

    [Handle]
    internal static Answer<LoginResult> On(in Login m, in Context ctx)
    {
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > MaxNameLength) return new LoginResult(false, default);

        // A real game verifies a token against an account store here.
        long hash = 17;
        foreach (char c in m.Name) hash = (hash * 31 + c) & 0x7FFF_FFFF;
        var player = new PlayerId(hash + 1); // ids start at 1

        // The player answers Attach once it bound the session (or fails): the login replies then, and a failed attach
        // fails the login with the player's status. Nothing waits here meanwhile.
        return ctx.ReplyAfter(ctx.Ask(player, new Attach(ctx.Caller)), new LoginResult(true, player));
    }
}
