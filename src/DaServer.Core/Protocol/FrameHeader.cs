namespace DaServer.Protocol;

/// <summary>Kind of a wire frame.</summary>
public enum FrameKind : byte
{
    /// <summary>
    /// A message to <see cref="FrameHeader.Target"/> (<see cref="FrameHeader.Key"/>): one-way, or a request when it
    /// carries a call id (<see cref="FrameFlags.HasCall"/>), answered by a <see cref="Reply"/> with that id.
    /// </summary>
    Message = 1,

    /// <summary>The answer to a request: its call id trailer, <see cref="FrameHeader.Status"/> (success or error), the reply payload.</summary>
    Reply = 2,

    /// <summary>
    /// The link protocol between nodes and between a client and its node (handshake, gossip, session operations):
    /// <see cref="FrameHeader.MessageId"/> is the operation; a call id makes it a request.
    /// </summary>
    Control = 3,
}

/// <summary>
/// Bits of <see cref="FrameHeader.Flags"/>. Trailers follow the payload: a frame is serialized once and routing data
/// that is known only later (the call id each hop assigns) is appended, or rewritten in place, without moving it.
/// </summary>
public static class FrameFlags
{
    /// <summary>The frame ends with a 4-byte call id (uint32): a request, its reply, or a control call.</summary>
    public const byte HasCall = 1;

    /// <summary>
    /// After the payload: the 16-byte <see cref="Origin"/> of the message (node uint64, id int64), set by the ingress
    /// that let it into the cluster and kept by every hop. Before the call id.
    /// </summary>
    public const byte HasOrigin = 2;

    /// <summary>
    /// After the payload, first of the trailers: the 8-byte call chain of a request (int64), the chain of the turn
    /// that asked: every request a turn makes, directly or through others, carries it, so a request that reaches an
    /// actor whose running turn has the same chain would wait for itself and fails at once (<see cref="RpcStatus.CallCycle"/>).
    /// </summary>
    public const byte HasChain = 4;

    /// <summary>The flags that name trailers.</summary>
    public const byte Trailers = HasCall | HasOrigin | HasChain;

    /// <summary>Bytes of the trailers a frame with <paramref name="flags"/> ends with.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TrailerSize(byte flags) =>
        ((flags & HasCall) != 0 ? sizeof(uint) : 0) + ((flags & HasOrigin) != 0 ? Origin.Size : 0) +
        ((flags & HasChain) != 0 ? sizeof(long) : 0);
}

/// <summary>
/// Fixed 24-byte little-endian frame header, then the payload, then the trailers named by <see cref="Flags"/>. Layout:
/// <code>
/// 0  int32  Length     whole frame size including header and trailers
/// 4  byte   Kind
/// 5  byte   Status     RpcStatus for replies; for requests the caller's time budget (see EncodeBudget), otherwise 0
/// 6  byte   Hops       forwarding count (loop guard)
/// 7  byte   Flags      FrameFlags (HasCall, HasOrigin, HasChain)
/// 8  uint32 Target     who handles a message: an actor or service handler type id, or an address scope (sessions,
///                      client groups); 0 in replies and control frames, and from clients that leave the choice to
///                      their node (the actor bound to the session, else the service)
/// 12 uint32 MessageId  type id of the message type (see TypeId<T>) or control op
/// 16 int64  Key        actor key / session id / group id (0 for services)
/// ..        payload
/// ..        int64 call chain when HasChain
/// ..        origin (uint64 node, int64 id) when HasOrigin
/// ..        uint32 call id when HasCall
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
    public uint Target;
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

    /// <summary>A message that expects a reply (it carries a call id).</summary>
    public readonly bool IsRequest => Kind == FrameKind.Message && (Flags & FrameFlags.HasCall) != 0;

    /// <summary>The time budget a request carries (zero: none).</summary>
    public readonly TimeSpan Budget => IsRequest ? TimeSpan.FromMilliseconds((byte)Status * BudgetUnitMs) : TimeSpan.Zero;

    /// <summary>Deadline (<see cref="Platform.Milliseconds"/>) of a request that arrived now, 0 when it has none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long DeadlineOnArrival() =>
        IsRequest && Status != 0 ? Platform.Milliseconds + (byte)Status * BudgetUnitMs : 0;

    /// <summary>The call id of a frame (<see cref="FrameFlags.HasCall"/>), 0 when it has none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CallIdOf(in FrameHeader header, ReadOnlySpan<byte> frame) =>
        (header.Flags & FrameFlags.HasCall) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(frame[(header.Length - sizeof(uint))..]) : 0;

    /// <summary>The origin trailer of a frame (<see cref="FrameFlags.HasOrigin"/>), default when it has none.</summary>
    public static Origin OriginOf(in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        if ((header.Flags & FrameFlags.HasOrigin) == 0) return default;
        var at = frame[(header.Length - FrameFlags.TrailerSize((byte)(header.Flags & ~FrameFlags.HasChain)))..];
        return new Origin(BinaryPrimitives.ReadUInt64LittleEndian(at), BinaryPrimitives.ReadInt64LittleEndian(at[sizeof(ulong)..]));
    }

    /// <summary>The payload of a frame: between the header and the trailers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> PayloadOf(in FrameHeader header, ReadOnlySpan<byte> frame) =>
        frame[Size..(header.Length - FrameFlags.TrailerSize(header.Flags))];

    /// <summary>
    /// Read the frame at the start of <paramref name="data"/>: true when it is there whole. False when it is not yet,
    /// with <paramref name="header"/>.Length the bytes it needs (0 while even its header is incomplete). Throws on a length
    /// outside [<see cref="Size"/>, <paramref name="maxFrameSize"/>]: a peer that sends it is broken or hostile.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReadFrame(ReadOnlySpan<byte> data, int maxFrameSize, out FrameHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        header = Read(data);
        if (header.Length < Size || header.Length > maxFrameSize) ThrowInvalidLength(header.Length);
        return header.Length <= data.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidLength(int length) => throw new InvalidOperationException("Invalid frame length " + length);

    public static FrameHeader Read(ReadOnlySpan<byte> s)
    {
        FrameHeader h;
        h.Length = BinaryPrimitives.ReadInt32LittleEndian(s);
        h.Kind = (FrameKind)s[4];
        h.Status = (RpcStatus)s[5];
        h.Hops = s[6];
        h.Flags = s[7];
        h.Target = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8));
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
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), Target);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), MessageId);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(16), Key);
    }
}
