using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DaServer.Protocol;

/// <summary>Kind of a wire frame.</summary>
public enum FrameKind : byte
{
    /// <summary>Call expecting a <see cref="Response"/> with the same request id.</summary>
    Request = 1,

    /// <summary>Fire-and-forget call (void methods).</summary>
    OneWay = 2,

    /// <summary>Reply to a <see cref="Request"/>; <see cref="FrameHeader.Status"/> tells success or error.</summary>
    Response = 3,

    /// <summary>Server -> one client session (key = session id on the gateway hop).</summary>
    Push = 4,

    /// <summary>Server -> every client session in a group (key = group id) on each gateway.</summary>
    Broadcast = 5,

    /// <summary>Runtime control traffic (handshake, gossip, session binding, groups). Method id = control op.</summary>
    Control = 6,

    Ping = 7,
    Pong = 8,
}

/// <summary>Bits of <see cref="FrameHeader.Flags"/>.</summary>
public static class FrameFlags
{
    /// <summary>Payload starts with the serialized <see cref="Rpc.SessionRef"/> of the client that made the call.</summary>
    public const byte HasCaller = 1;
}

/// <summary>Status carried by <see cref="FrameKind.Response"/> frames.</summary>
public enum RpcStatus : byte
{
    Ok = 0,
    /// <summary>Handler threw; payload = message string.</summary>
    Error = 1,
    UnknownMethod = 2,
    /// <summary>Target actor/service not hosted here (stale membership view); the caller should retry.</summary>
    NotHosted = 3,
    Timeout = 4,
    /// <summary>Client is not allowed to call this contract or has no bound key.</summary>
    Forbidden = 5,
    /// <summary>Connection lost before the response arrived.</summary>
    Disconnected = 6,
}

/// <summary>
/// Fixed 24-byte little-endian frame header. Layout:
/// <code>
/// 0  int32  Length     whole frame size including header
/// 4  byte   Kind
/// 5  byte   Status     RpcStatus for responses, otherwise 0
/// 6  byte   Hops       forwarding count (loop guard)
/// 7  byte   Flags      reserved
/// 8  uint32 RequestId  correlation id (0 for one-way / push)
/// 12 uint32 MethodId   globally unique method id (see RpcIds) or control op
/// 16 int64  Key        actor key / session id / group id
/// </code>
/// </summary>
public struct FrameHeader
{
    public const int Size = 24;

    /// <summary>Hard upper bound of a single frame, protects against corrupt or hostile length prefixes.</summary>
    public const int MaxFrameSize = 16 * 1024 * 1024;

    public int Length;
    public FrameKind Kind;
    public RpcStatus Status;
    public byte Hops;
    public byte Flags;
    public uint RequestId;
    public uint MethodId;
    public long Key;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FrameHeader Read(ReadOnlySpan<byte> s)
    {
        FrameHeader h;
        h.Length = BinaryPrimitives.ReadInt32LittleEndian(s);
        h.Kind = (FrameKind)s[4];
        h.Status = (RpcStatus)s[5];
        h.Hops = s[6];
        h.Flags = s[7];
        h.RequestId = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8));
        h.MethodId = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12));
        h.Key = BinaryPrimitives.ReadInt64LittleEndian(s.Slice(16));
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Write(Span<byte> s)
    {
        BinaryPrimitives.WriteInt32LittleEndian(s, Length);
        s[4] = (byte)Kind;
        s[5] = (byte)Status;
        s[6] = Hops;
        s[7] = Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), RequestId);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), MethodId);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(16), Key);
    }

    /// <summary>Overwrite only the routing fields of an already-serialized frame (used when forwarding).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Patch(Span<byte> frame, FrameKind kind, uint requestId, long key)
    {
        frame[4] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(8), requestId);
        BinaryPrimitives.WriteInt64LittleEndian(frame.Slice(16), key);
    }
}
