namespace DaServer.Protocol;

/// <summary>Kind of a wire frame.</summary>
public enum FrameKind : byte
{
    /// <summary>
    /// Message expecting a <see cref="Response"/> with the same request id (<c>Ask</c>): message id, key; an actor's
    /// handler type trails the payload (<see cref="FrameFlags.ToActor"/>), a service is the one handling the message.
    /// </summary>
    Request = 1,

    /// <summary>Client to gateway: one-way message (the gateway turns it into a <see cref="Message"/>).</summary>
    OneWay = 2,

    /// <summary>Reply to a <see cref="Request"/>; <see cref="FrameHeader.Status"/> tells success or error.</summary>
    Response = 3,

    /// <summary>Server -> one client session (key = session id on the gateway hop).</summary>
    Push = 4,

    /// <summary>Server -> every client session in a group (key = group id) on each gateway.</summary>
    Broadcast = 5,

    /// <summary>Runtime control traffic (handshake, gossip, session binding, groups). Method id = control op.</summary>
    Control = 6,

    /// <summary>
    /// One-way message for one handler type (<c>Send</c>, <c>Publish</c>): message id, request id = target
    /// handler type id (one-way, so the correlation field is free), key = actor key (0 for services).
    /// </summary>
    Message = 9,
}

/// <summary>Bits of <see cref="FrameHeader.Flags"/>.</summary>
public static class FrameFlags
{
    /// <summary>Payload starts with the serialized <see cref="Rpc.SessionRef"/> of the client that made the call.</summary>
    public const byte HasCaller = 1;

    /// <summary>
    /// A <see cref="FrameKind.Request"/> for an actor: the frame ends with the actor's handler type id (uint32; the
    /// header's RequestId is the call's). Without it, a request is for the one service handling its message.
    /// </summary>
    public const byte ToActor = 2;
}

/// <summary>Status carried by <see cref="FrameKind.Response"/> frames.</summary>
public enum RpcStatus : byte
{
    Ok = 0,
    /// <summary>Handler threw; payload = message string.</summary>
    Error = 1,
    /// <summary>Target actor/service not hosted here (stale membership view); the caller should retry.</summary>
    NotHosted = 3,
    Timeout = 4,
    /// <summary>Client is not allowed to call this contract or has no bound key.</summary>
    Forbidden = 5,
    /// <summary>Connection lost before the response arrived.</summary>
    Disconnected = 6,
    /// <summary>The target shard or actor mailbox is full; nothing ran. The caller may back off and retry.</summary>
    Overloaded = 7,
}

/// <summary>
/// Fixed 24-byte little-endian frame header. Layout:
/// <code>
/// 0  int32  Length     whole frame size including header
/// 4  byte   Kind
/// 5  byte   Status     RpcStatus for responses; for requests the caller's time budget (see EncodeBudget), otherwise 0
/// 6  byte   Hops       forwarding count (loop guard)
/// 7  byte   Flags      FrameFlags (HasCaller, ToActor)
/// 8  uint32 RequestId  correlation id (requests); target handler type id (Message); 0 otherwise
/// 12 uint32 MessageId  wire id of the message type (see WireIds) or control op
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
    public uint MessageId;
    public long Key;

    /// <summary>Milliseconds per unit of a request's time budget.</summary>
    public const int BudgetUnitMs = 100;

    /// <summary>
    /// The status byte of a request (free there): the caller's remaining time in units of <see cref="BudgetUnitMs"/>
    /// (at least one unit while time remains, at most 25.5 s), 0 = no deadline. Relative, so clocks of different
    /// machines need not agree; the receiver turns it into a deadline on arrival.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RpcStatus EncodeBudget(long remainingMs) =>
        (RpcStatus)(byte)Math.Clamp((remainingMs + BudgetUnitMs - 1) / BudgetUnitMs, 1, byte.MaxValue);

    /// <summary>Deadline (<see cref="Environment.TickCount64"/>) of a request that arrived now, 0 when it has none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long DeadlineOnArrival() =>
        Kind == FrameKind.Request && Status != 0 ? Environment.TickCount64 + (byte)Status * BudgetUnitMs : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>
    /// The handler type a frame targets: a Message frame's RequestId, an actor request's trailer
    /// (<see cref="FrameFlags.ToActor"/>), 0 for a service request (its message names it).
    /// </summary>
    public static uint TargetOf(in FrameHeader header, ReadOnlySpan<byte> frame) =>
        header.Kind == FrameKind.Message ? header.RequestId
        : (header.Flags & FrameFlags.ToActor) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(frame[(header.Length - sizeof(uint))..])
        : 0;

    public static FrameHeader Read(ReadOnlySpan<byte> s)
    {
        FrameHeader h;
        h.Length = BinaryPrimitives.ReadInt32LittleEndian(s);
        h.Kind = (FrameKind)s[4];
        h.Status = (RpcStatus)s[5];
        h.Hops = s[6];
        h.Flags = s[7];
        h.RequestId = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8));
        h.MessageId = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12));
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
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), MessageId);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(16), Key);
    }
}
