namespace DaServer.Runtime;

/// <summary>
/// A client's proof that it owned a session, so any gateway can resume it after a lost connection (ADR-036): a
/// <see cref="SignedToken"/> issued by the session's gateway after every binding change; gateways keep no state for it.
/// </summary>
/// <remarks>Content (little endian): gateway node id, session id, binding count, bindings (handler type id, key).</remarks>
internal static class ReconnectTicket
{
    private const int HeaderSize = 8 + 8 + 4;
    private const int BindingSize = 4 + 8;

    /// <summary>The cluster stays small: a session is bound to a handful of actor types.</summary>
    public const int MaxBindings = 64;

    public static int SizeOf(int bindings) => SignedToken.SizeOf(HeaderSize + bindings * BindingSize);

    /// <summary>Write the ticket of <paramref name="session"/> into <paramref name="destination"/> (<see cref="SizeOf"/> bytes).</summary>
    public static void Write(Span<byte> destination, byte[] secret, SessionRef session, ReadOnlySpan<SessionBinding> bindings,
        long nowMs, long lifetimeMs)
    {
        var content = SignedToken.Content(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(content, session.GatewayNodeId);
        BinaryPrimitives.WriteInt64LittleEndian(content[8..], session.SessionId);
        BinaryPrimitives.WriteInt32LittleEndian(content[16..], bindings.Length);
        int offset = HeaderSize;
        foreach (var binding in bindings)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(content[offset..], binding.HandlerTypeId);
            BinaryPrimitives.WriteInt64LittleEndian(content[(offset + 4)..], binding.Key);
            offset += BindingSize;
        }

        SignedToken.Sign(destination, secret, nowMs, lifetimeMs);
    }

    /// <summary>
    /// Check signature, shape and expiry. On success <paramref name="session"/> is the session it was issued for and
    /// <paramref name="bindings"/> its bindings (a slice of the ticket: read them with <see cref="BindingAt"/>).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> ticket, byte[] secret, long nowMs, out SessionRef session, out ReadOnlySpan<byte> bindings)
    {
        session = default;
        bindings = default;
        if (!SignedToken.TryRead(ticket, secret, nowMs, out var content) || content.Length < HeaderSize) return false;
        int count = BinaryPrimitives.ReadInt32LittleEndian(content[16..]);
        if (count < 0 || count > MaxBindings || content.Length != HeaderSize + count * BindingSize) return false;

        session = new SessionRef(BinaryPrimitives.ReadUInt64LittleEndian(content), BinaryPrimitives.ReadInt64LittleEndian(content[8..]));
        bindings = content[HeaderSize..];
        return true;
    }

    /// <summary>Binding <paramref name="index"/> of the bindings returned by <see cref="TryRead"/>.</summary>
    public static SessionBinding BindingAt(ReadOnlySpan<byte> bindings, int index)
    {
        var b = bindings.Slice(index * BindingSize, BindingSize);
        return new SessionBinding(BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b[4..]));
    }

    public static int BindingCount(ReadOnlySpan<byte> bindings) => bindings.Length / BindingSize;
}
