using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using DaServer.Rpc;

namespace DaServer.Runtime;

/// <summary>
/// A client's proof that it owned a session, so any gateway can resume it after a lost connection (ADR-036). Issued
/// by the session's gateway after every binding change, signed with the cluster secret (HMAC-SHA256); gateways keep
/// no state for it. The content is not secret (the client reads the expiry), only unforgeable.
/// </summary>
/// <remarks>
/// Layout (little endian): issued and expires (Unix milliseconds), gateway node id, session id, binding count,
/// bindings (implementation id, key), HMAC-SHA256 of everything before it.
/// </remarks>
internal static class ReconnectTicket
{
    private const int HeaderSize = 8 + 8 + 8 + 8 + 4;
    private const int BindingSize = 4 + 8;
    private const int MacSize = 32;

    /// <summary>The cluster stays small: a session is bound to a handful of actor types.</summary>
    public const int MaxBindings = 64;

    public static int SizeOf(int bindings) => HeaderSize + bindings * BindingSize + MacSize;

    /// <summary>Write the ticket of <paramref name="session"/> into <paramref name="destination"/> (<see cref="SizeOf"/> bytes).</summary>
    public static void Write(Span<byte> destination, byte[] secret, SessionRef session, ReadOnlySpan<SessionBinding> bindings,
        long nowMs, long lifetimeMs)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, nowMs);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], nowMs + lifetimeMs);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], session.GatewayNodeId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], session.SessionId);
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..], bindings.Length);
        int offset = HeaderSize;
        foreach (var binding in bindings)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], binding.ImplementationId);
            BinaryPrimitives.WriteInt64LittleEndian(destination[(offset + 4)..], binding.Key);
            offset += BindingSize;
        }

        HMACSHA256.HashData(secret, destination[..offset], destination[offset..]);
    }

    /// <summary>
    /// Check signature, shape and expiry. On success <paramref name="session"/> is the session it was issued for and
    /// <paramref name="bindings"/> its bindings (a slice of the ticket: read them with <see cref="BindingAt"/>).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> ticket, byte[] secret, long nowMs, out SessionRef session, out ReadOnlySpan<byte> bindings)
    {
        session = default;
        bindings = default;
        if (ticket.Length < SizeOf(0)) return false;
        int count = BinaryPrimitives.ReadInt32LittleEndian(ticket[32..]);
        if (count < 0 || count > MaxBindings || ticket.Length != SizeOf(count)) return false;

        int signed = ticket.Length - MacSize;
        Span<byte> mac = stackalloc byte[MacSize];
        HMACSHA256.HashData(secret, ticket[..signed], mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, ticket[signed..])) return false;
        if (ExpiresMs(ticket) <= nowMs) return false;

        session = new SessionRef(BinaryPrimitives.ReadUInt64LittleEndian(ticket[16..]), BinaryPrimitives.ReadInt64LittleEndian(ticket[24..]));
        bindings = ticket[HeaderSize..signed];
        return true;
    }

    /// <summary>Binding <paramref name="index"/> of the bindings returned by <see cref="TryRead"/>.</summary>
    public static SessionBinding BindingAt(ReadOnlySpan<byte> bindings, int index)
    {
        var b = bindings.Slice(index * BindingSize, BindingSize);
        return new SessionBinding(BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b[4..]));
    }

    public static int BindingCount(ReadOnlySpan<byte> bindings) => bindings.Length / BindingSize;

    public static long IssuedMs(ReadOnlySpan<byte> ticket) => BinaryPrimitives.ReadInt64LittleEndian(ticket);

    public static long ExpiresMs(ReadOnlySpan<byte> ticket) => BinaryPrimitives.ReadInt64LittleEndian(ticket[8..]);
}
