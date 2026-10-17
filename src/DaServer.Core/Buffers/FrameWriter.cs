using System.Runtime.InteropServices;

namespace DaServer.Buffers;

/// <summary>
/// Pooled frame builder. The first <see cref="FrameHeader.Size"/> bytes are reserved for the header so a frame is
/// serialized exactly once, contiguously, and can be handed to a local mailbox (zero copy) or copied into a socket
/// batch. Its bytes live in a <see cref="NinoArrayBufferWriter"/>, which Nino writes into in place (<see cref="Write{T}"/>);
/// the writer stays with the pooled frame, so a steady state allocates nothing.
/// </summary>
/// <remarks>Ownership: whoever holds the instance must call <see cref="Dispose"/> exactly once.</remarks>
public sealed class FrameWriter : IDisposable
{
    /// <summary>Frames in flight per process (queued in shards, mailboxes and links): bursts below it allocate once.</summary>
    private static readonly ObjectPool<FrameWriter> Pool = new ObjectPool<FrameWriter>(() => new FrameWriter(), 1 << 16);

    /// <summary>
    /// A pooled writer keeps at most this much of an array: a frame that grew past it gives its array back to
    /// <c>ArrayPool</c> on dispose (the next rent takes one from there again), so the pool holds at most
    /// <c>65,536 x 4 KB</c> whatever the bursts of large frames were.
    /// </summary>
    private const int RetainedCapacity = 4 * 1024;

    private readonly NinoArrayBufferWriter _out = new(256);
    private bool _rented;
    private byte _trailers; // FrameFlags of the trailers written so far (written into the header by SetHeader)

    /// <summary>
    /// When the request in this frame is no longer worth running (<see cref="Platform.Milliseconds"/>; 0 = never):
    /// its caller has given up. Set where the request enters this process, checked right before it runs.
    /// </summary>
    internal long Deadline;

    private FrameWriter()
    {
    }

    /// <summary>Rent a writer positioned right after the reserved header.</summary>
    public static FrameWriter Rent(int sizeHint = 256)
    {
        var w = Rented();
        w._out.EnsureCapacity(Math.Max(sizeHint, FrameHeader.Size + 16));
        w._out.Advance(FrameHeader.Size);
        return w;
    }

    /// <summary>Rent a writer holding a copy of a complete received frame (header and trailers included).</summary>
    public static FrameWriter CopyOf(ReadOnlySpan<byte> frame)
    {
        var w = Rented();
        w.WriteRaw(frame);
        w._trailers = (byte)(frame[7] & FrameFlags.Trailers);
        return w;
    }

    private static FrameWriter Rented()
    {
        var w = Pool.Rent();
        w._out.ResetWrittenCount();
        w._rented = true;
        w._trailers = 0;
        w.Deadline = 0;
        return w;
    }

    /// <summary>Serialize <paramref name="value"/> into the payload (before any trailer), in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(T value) => NinoSerializer.Serialize(value, _out);

    /// <summary>The written bytes, writable (the header and the trailers are patched in place).</summary>
    private Span<byte> Bytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MemoryMarshal.AsMemory(_out.WrittenMemory).Span;
    }

    /// <summary>Whole frame including header and trailers.</summary>
    public ReadOnlySpan<byte> Frame => _out.WrittenSpan;

    /// <summary>The whole frame as memory (valid until this writer is disposed): for writes that complete later.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _out.WrittenMemory;

    /// <summary>Payload: after the header, before the trailers.</summary>
    public ReadOnlySpan<byte> Payload => _out.WrittenSpan[FrameHeader.Size..(Length - FrameFlags.TrailerSize(_trailers))];

    /// <summary>The call id trailer (<see cref="FrameFlags.HasCall"/>), 0 when the frame has none.</summary>
    public uint CallId => (_trailers & FrameFlags.HasCall) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(Frame[(Length - sizeof(uint))..]) : 0;

    /// <summary>The origin trailer (<see cref="FrameFlags.HasOrigin"/>), default when the frame has none.</summary>
    public Origin Origin
    {
        get
        {
            if ((_trailers & FrameFlags.HasOrigin) == 0) return default;
            var at = Frame[(Length - FrameFlags.TrailerSize((byte)(_trailers & ~FrameFlags.HasChain)))..];
            return new Origin(BinaryPrimitives.ReadUInt64LittleEndian(at), BinaryPrimitives.ReadInt64LittleEndian(at[sizeof(ulong)..]));
        }
    }

    /// <summary>The call chain trailer (<see cref="FrameFlags.HasChain"/>), 0 when the frame has none.</summary>
    public long Chain =>
        (_trailers & FrameFlags.HasChain) != 0 ? BinaryPrimitives.ReadInt64LittleEndian(Frame[(Length - FrameFlags.TrailerSize(_trailers))..]) : 0;

    public int Length => _out.WrittenCount;

    /// <summary>Finish a control frame (a call when <paramref name="callId"/> is set), send it and return this writer to the pool.</summary>
    internal void SendControl(IFrameSender target, uint op, long key = 0, uint callId = 0)
    {
        if (callId != 0) SetCallId(callId);
        SetHeader(FrameKind.Control, 0, op, key);
        target.Send(Frame);
        Dispose();
    }

    /// <summary>Finish the reply to <paramref name="callId"/> (its key: <paramref name="key"/>), send it and return this writer to the pool.</summary>
    internal void SendReply(IFrameSender target, uint callId, RpcStatus status, long key = 0)
    {
        SetCallId(callId);
        SetHeader(FrameKind.Reply, 0, 0, key, status);
        target.Send(Frame);
        Dispose();
    }

    /// <summary>
    /// Write the header (length is filled in automatically; the trailer flags written so far are added to
    /// <paramref name="flags"/>). Call after the payload and the trailers.
    /// </summary>
    public void SetHeader(FrameKind kind, uint target, uint messageId, long key, RpcStatus status = RpcStatus.Ok,
        byte flags = 0, byte hops = 0)
    {
        if (Length > FrameHeader.MaxFrameSize) ThrowTooLarge();
        var h = new FrameHeader
        {
            Length = Length,
            Kind = kind,
            Status = status,
            Hops = hops,
            Flags = (byte)((flags & ~FrameFlags.Trailers) | _trailers),
            Target = target,
            MessageId = messageId,
            Key = key,
        };
        h.Write(Bytes);
    }

    /// <summary>
    /// Set the call id trailer (after the payload): appended once, rewritten in place by every hop that relays the
    /// request under its own call id.
    /// </summary>
    public void SetCallId(uint callId) => BinaryPrimitives.WriteUInt32LittleEndian(Trailer(FrameFlags.HasCall, sizeof(uint), 0), callId);

    /// <summary>
    /// Set the origin trailer (after the payload, before the call id): written once, by the ingress that lets the message
    /// into the cluster.
    /// </summary>
    public void SetOrigin(Origin origin)
    {
        var at = Trailer(FrameFlags.HasOrigin, Origin.Size, FrameFlags.HasCall);
        BinaryPrimitives.WriteUInt64LittleEndian(at, origin.Node);
        BinaryPrimitives.WriteInt64LittleEndian(at[sizeof(ulong)..], origin.Id);
    }

    /// <summary>Set the call chain trailer (after the payload, before the origin): written by the node that sends the request.</summary>
    public void SetChain(long chain) =>
        BinaryPrimitives.WriteInt64LittleEndian(Trailer(FrameFlags.HasChain, sizeof(long), FrameFlags.HasCall | FrameFlags.HasOrigin), chain);

    /// <summary>
    /// The bytes of trailer <paramref name="flag"/>, inserted on first use in front of the trailers named by
    /// <paramref name="following"/> (which move back by <paramref name="size"/>).
    /// </summary>
    private Span<byte> Trailer(byte flag, int size, byte following)
    {
        int after = FrameFlags.TrailerSize((byte)(_trailers & following));
        if ((_trailers & flag) == 0)
        {
            _out.GetSpan(size);
            _out.Advance(size);
            var bytes = Bytes;
            bytes.Slice(Length - size - after, after).CopyTo(bytes[(Length - after)..]);
            _trailers |= flag;
        }

        return Bytes.Slice(Length - after - size, size);
    }

    /// <summary>Bytes already written at <paramref name="offset"/> (to patch a length written ahead of its data).</summary>
    internal Span<byte> Written(int offset, int length) => Bytes.Slice(offset, length);

    /// <summary>Append raw bytes to the payload (before any trailer).</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_out.GetSpan(bytes.Length));
        _out.Advance(bytes.Length);
    }

    /// <summary>Room for at least <paramref name="sizeHint"/> raw bytes at the end of the payload; <see cref="Advance"/> commits them.</summary>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Length + Math.Max(sizeHint, 1) > FrameHeader.MaxFrameSize) ThrowTooLarge();
        return _out.GetSpan(sizeHint);
    }

    public void Advance(int count) => _out.Advance(count);

    public void Dispose()
    {
        if (!_rented)
            return; // double dispose guard

        _rented = false;
        _out.ResetWrittenCount();
        if (_out.Capacity > RetainedCapacity) _out.Dispose(); // the array goes back to ArrayPool; the writer stays usable
        Pool.Return(this);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTooLarge() => throw new InvalidOperationException("Frame exceeds FrameHeader.MaxFrameSize");
}
