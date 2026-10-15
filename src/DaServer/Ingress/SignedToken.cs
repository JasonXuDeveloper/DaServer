using System.Security.Cryptography;

namespace DaServer.Ingress;

/// <summary>
/// Data an edge hands out and later trusts again without keeping state (resume tickets, admission tokens, internal
/// HTTP auth): the content with when it was issued and when it expires, signed with the cluster secret (HMAC-SHA256).
/// Not encrypted: whoever holds it may read it, nobody can forge or extend it. Allocation-free.
/// </summary>
/// <remarks>Layout (little endian): issued, expires (Unix milliseconds), content, HMAC-SHA256 of everything before it.</remarks>
public static class SignedToken
{
    private const int TimesSize = 16;
    private const int MacSize = 32;

    /// <summary>Bytes a token adds to its content.</summary>
    public const int Overhead = TimesSize + MacSize;

    /// <summary>Size of a token with <paramref name="contentLength"/> bytes of content.</summary>
    public static int SizeOf(int contentLength) => contentLength + Overhead;

    /// <summary>The content area of a token being written (fill it, then call <see cref="Sign"/>).</summary>
    public static Span<byte> Content(Span<byte> token) => token[TimesSize..^MacSize];

    /// <summary>Stamp and sign <paramref name="token"/> (<see cref="SizeOf"/> bytes, its <see cref="Content"/> filled).</summary>
    public static void Sign(Span<byte> token, ReadOnlySpan<byte> secret, long nowMs, long lifetimeMs)
    {
        BinaryPrimitives.WriteInt64LittleEndian(token, nowMs);
        BinaryPrimitives.WriteInt64LittleEndian(token[8..], nowMs + lifetimeMs);
        HMACSHA256.HashData(secret, token[..^MacSize], token[^MacSize..]);
    }

    /// <summary>
    /// Check signature and expiry; <paramref name="content"/> is the signed content (a slice of
    /// <paramref name="token"/>) when it holds.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> token, ReadOnlySpan<byte> secret, long nowMs, out ReadOnlySpan<byte> content)
    {
        content = default;
        if (token.Length < Overhead) return false;
        Span<byte> mac = stackalloc byte[MacSize];
        HMACSHA256.HashData(secret, token[..^MacSize], mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, token[^MacSize..]) || ExpiresMs(token) <= nowMs) return false;
        content = token[TimesSize..^MacSize];
        return true;
    }

    public static long IssuedMs(ReadOnlySpan<byte> token) => BinaryPrimitives.ReadInt64LittleEndian(token);

    public static long ExpiresMs(ReadOnlySpan<byte> token) => BinaryPrimitives.ReadInt64LittleEndian(token[8..]);
}
