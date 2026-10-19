namespace DaServer.Clients;

/// <summary>
/// A client's proof that it owned a session, so any client-facing node can resume it after a lost connection (ADR-036): a
/// <see cref="SignedToken"/> issued by the session's node after every grant change; nodes keep no state for it.
/// </summary>
/// <remarks>Content (little endian): node id, session id, grant count, grants (handler type id, key, bound).</remarks>
internal static class ResumeTicket
{
    private const int HeaderSize = 8 + 8 + 4;
    private const int GrantSize = 4 + 8 + 1;

    public static int SizeOf(int grants) => SignedToken.SizeOf(HeaderSize + grants * GrantSize);

    /// <summary>Write the ticket of <paramref name="session"/> into <paramref name="destination"/> (<see cref="SizeOf"/> bytes).</summary>
    public static void Write(Span<byte> destination, byte[] secret, ClientSession session, ReadOnlySpan<SessionGrant> grants,
        long nowMs, long lifetimeMs)
    {
        var content = SignedToken.Content(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(content, session.Node);
        BinaryPrimitives.WriteInt64LittleEndian(content[8..], session.Id);
        BinaryPrimitives.WriteInt32LittleEndian(content[16..], grants.Length);
        int offset = HeaderSize;
        foreach (var grant in grants)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(content[offset..], grant.HandlerTypeId);
            BinaryPrimitives.WriteInt64LittleEndian(content[(offset + 4)..], grant.Key);
            content[offset + 12] = grant.Bound ? (byte)1 : (byte)0;
            offset += GrantSize;
        }

        SignedToken.Sign(destination, secret, nowMs, lifetimeMs);
    }

    /// <summary>
    /// Check signature (by any of <paramref name="keys"/>: the current key, and the accepted one while a rotation runs), shape and
    /// expiry. On success <paramref name="session"/> is the session it was issued for and <paramref name="grants"/> its
    /// grants (a slice of the ticket: read them with <see cref="GrantAt"/>).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> ticket, byte[][] keys, long nowMs, out ClientSession session,
        out ReadOnlySpan<byte> grants)
    {
        session = default;
        grants = default;
        ReadOnlySpan<byte> content = default;
        bool signed = false;
        foreach (var key in keys)
            if (signed = SignedToken.TryRead(ticket, key, nowMs, out content)) break;
        if (!signed || content.Length < HeaderSize) return false;
        int count = BinaryPrimitives.ReadInt32LittleEndian(content[16..]);
        if (count < 0 || count > SessionState.MaxGrants || content.Length != HeaderSize + count * GrantSize) return false;

        session = new ClientSession(BinaryPrimitives.ReadUInt64LittleEndian(content), BinaryPrimitives.ReadInt64LittleEndian(content[8..]));
        grants = content[HeaderSize..];
        return true;
    }

    /// <summary>Grant <paramref name="index"/> of the grants returned by <see cref="TryRead"/>.</summary>
    public static SessionGrant GrantAt(ReadOnlySpan<byte> grants, int index)
    {
        var g = grants.Slice(index * GrantSize, GrantSize);
        return new SessionGrant(BinaryPrimitives.ReadUInt32LittleEndian(g), BinaryPrimitives.ReadInt64LittleEndian(g[4..]), g[12] != 0);
    }

    public static int GrantCount(ReadOnlySpan<byte> grants) => grants.Length / GrantSize;
}
