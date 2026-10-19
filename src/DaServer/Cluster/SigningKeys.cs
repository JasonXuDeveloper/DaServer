namespace DaServer.Cluster;

/// <summary>
/// The cluster's signing secrets as keys (ADR-065): the current one signs, and while a rotation runs the accepted one
/// verifies too (<see cref="NodeOptions.AcceptedSigningSecret"/>). One place for what links, resume tickets and the
/// admin token each did themselves: try the current key, then the accepted one.
/// </summary>
internal sealed class SigningKeys
{
    private SigningKeys(byte[]? current, byte[][] verifying) => (Current, Verifying) = (current, verifying);

    /// <summary>The key that signs; null without a secret (nothing is signed, nothing is checked).</summary>
    public byte[]? Current { get; }

    /// <summary>The keys a signature may be made with: the current one first, then the accepted one.</summary>
    public byte[][] Verifying { get; }

    /// <summary>The keys of <paramref name="options"/>' secrets, each first turned into what is signed with (<paramref name="derive"/>).</summary>
    public static SigningKeys Of(NodeOptions options, Func<string, string>? derive = null)
    {
        byte[]? Key(string? secret) => secret is { Length: > 0 } ? System.Text.Encoding.UTF8.GetBytes(derive == null ? secret : derive(secret)) : null;
        byte[]? current = Key(options.SigningSecret), accepted = Key(options.AcceptedSigningSecret);
        return new SigningKeys(current, current == null ? [] : accepted == null ? [current] : [current, accepted]);
    }
}
