using System.Security.Cryptography;
using System.Text;

namespace DaServer.Hosting;

/// <summary>
/// The bearer token of a node's admin endpoints: HMAC-SHA256 of a fixed label under the cluster's signing secret, in hex.
/// Whoever holds the secret (the nodes, the AppHost that generated it) derives it; the token itself does not give the
/// secret away, so it signs no resume ticket. Compiled into DaServer.Aspire too (its dashboard commands send it).
/// </summary>
internal static class AdminToken
{
    public static string For(string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), "daserver-admin"u8));
}
