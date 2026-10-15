namespace DaServer.Buffers;

/// <summary>
/// Pooled frame builder. The first <see cref="FrameHeader.Size"/> bytes are reserved for the header so a frame is
/// serialized exactly once, contiguously, and can be handed to a local mailbox (zero copy) or copied into a socket
/// batch. Implements <see cref="INinoBufferWriter"/> so generated code serializes arguments straight into it.
/// </summary>
/// <remarks>Ownership: whoever holds the instance must call <see cref="Dispose"/> exactly once.</remarks>
public sealed class FrameWriter : INinoBufferWriter, IDisposable
{
    /// <summary>Frames in flight per process (queued in shards, mailboxes and links): bursts below it allocate once.</summary>
    private static readonly ObjectPool<FrameWriter> Pool = new ObjectPool<FrameWriter>(() => new FrameWriter(), 1 << 16);

    private byte[] _buffer = Array.Empty<byte>();
    private int _pos;
    private byte _trailers; // FrameFlags of the trailers written so far (written into the header by SetHeader)

    /// <summary>
    /// When the request in this frame is no longer worth running (<see cref="Environment.TickCount64"/>; 0 = never):
    /// its caller has given up. Set where the request enters this process, checked right before it runs.
    /// </summary>
    internal long Deadline;

    private FrameWriter()
    {
    }

    /// <summary>Rent a writer positioned right after the reserved header.</summary>
    public static FrameWriter Rent(int sizeHint = 256)
    {
        var w = Pool.Rent();
        w._buffer = BufferPool.Rent(Math.Max(sizeHint, FrameHeader.Size + 16));
        w._pos = FrameHeader.Size;
        w._trailers = 0;
        w.Deadline = 0;
        return w;
    }

    /// <summary>Rent a writer holding a copy of a complete received frame (header and trailers included).</summary>
    public static FrameWriter CopyOf(ReadOnlySpan<byte> frame)
    {
        var w = Pool.Rent();
        w._buffer = BufferPool.Rent(Math.Max(frame.Length, FrameHeader.Size));
        frame.CopyTo(w._buffer);
        w._pos = frame.Length;
        w._trailers = (byte)(frame[7] & FrameFlags.Trailers);
        w.Deadline = 0;
        return w;
    }

    /// <summary>Nino writer appending to this frame. Cheap to create; create one per serialization pass.</summary>
    public Writer Nino
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new Writer(this);
    }

    /// <summary>Whole frame including header and trailers.</summary>
    public ReadOnlySpan<byte> Frame => new ReadOnlySpan<byte>(_buffer, 0, _pos);

    /// <summary>Payload: after the header, before the trailers.</summary>
    public ReadOnlySpan<byte> Payload =>
        new ReadOnlySpan<byte>(_buffer, FrameHeader.Size, _pos - FrameHeader.Size - FrameFlags.TrailerSize(_trailers));

    /// <summary>The call id trailer (<see cref="FrameFlags.HasCall"/>), 0 when the frame has none.</summary>
    public uint CallId => (_trailers & FrameFlags.HasCall) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_pos - sizeof(uint))) : 0;

    /// <summary>The origin trailer (<see cref="FrameFlags.HasOrigin"/>), default when the frame has none.</summary>
    public Origin Origin
    {
        get
        {
            if ((_trailers & FrameFlags.HasOrigin) == 0) return default;
            var at = _buffer.AsSpan(_pos - FrameFlags.TrailerSize(_trailers));
            return new Origin(BinaryPrimitives.ReadUInt64LittleEndian(at), BinaryPrimitives.ReadInt64LittleEndian(at[sizeof(ulong)..]));
        }
    }

    public int Length => _pos;

    /// <summary>Finish a control frame (a call when <paramref name="callId"/> is set), send it and return this writer to the pool.</summary>
    internal void SendControl(IFrameSender target, uint op, long key = 0, uint callId = 0)
    {
        if (callId != 0) SetCallId(callId);
        SetHeader(FrameKind.Control, 0, op, key);
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
        var h = new FrameHeader
        {
            Length = _pos,
            Kind = kind,
            Status = status,
            Hops = hops,
            Flags = (byte)((flags & ~FrameFlags.Trailers) | _trailers),
            Target = target,
            MessageId = messageId,
            Key = key,
        };
        h.Write(_buffer);
    }

    /// <summary>
    /// Set the call id trailer (after the payload): appended once, rewritten in place by every hop that relays the
    /// request under its own call id.
    /// </summary>
    public void SetCallId(uint callId)
    {
        if ((_trailers & FrameFlags.HasCall) == 0)
        {
            GetSpan(sizeof(uint));
            _pos += sizeof(uint);
            _trailers |= FrameFlags.HasCall;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_pos - sizeof(uint)), callId);
    }

    /// <summary>
    /// Set the origin trailer (after the payload, before the call id): written once, by the ingress that lets the message
    /// into the cluster.
    /// </summary>
    public void SetOrigin(Origin origin)
    {
        if ((_trailers & FrameFlags.HasOrigin) == 0)
        {
            uint callId = CallId;
            if ((_trailers & FrameFlags.HasCall) != 0) _pos -= sizeof(uint); // the call id moves behind the origin
            GetSpan(Origin.Size);
            _pos += Origin.Size;
            _trailers |= FrameFlags.HasOrigin;
            if ((_trailers & FrameFlags.HasCall) != 0)
            {
                _trailers &= unchecked((byte)~FrameFlags.HasCall);
                SetCallId(callId);
            }
        }

        var at = _buffer.AsSpan(_pos - FrameFlags.TrailerSize(_trailers));
        BinaryPrimitives.WriteUInt64LittleEndian(at, origin.Node);
        BinaryPrimitives.WriteInt64LittleEndian(at[sizeof(ulong)..], origin.Id);
    }

    /// <summary>Append raw bytes to the payload (before any trailer).</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _pos += bytes.Length;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        if (buffer.Length == 0)
            return; // double dispose guard

        _buffer = Array.Empty<byte>();
        _pos = 0;
        BufferPool.Return(buffer);
        Pool.Return(this);
    }

    // ---- INinoBufferWriter / IBufferWriter<byte> ----

    public int WrittenCount => _pos;

    public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(_buffer, 0, _pos);

    public ReadOnlyMemory<byte> WrittenMemory => new ReadOnlyMemory<byte>(_buffer, 0, _pos);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => _pos += count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return new Memory<byte>(_buffer, _pos, _buffer.Length - _pos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return new Span<byte>(_buffer, _pos, _buffer.Length - _pos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - _pos < sizeHint)
            Grow(sizeHint);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int sizeHint)
    {
        int newSize = Math.Max(_buffer.Length * 2, _pos + sizeHint);
        if (newSize > FrameHeader.MaxFrameSize)
            throw new InvalidOperationException("Frame exceeds FrameHeader.MaxFrameSize");

        var next = BufferPool.Rent(newSize);
        System.Buffer.BlockCopy(_buffer, 0, next, 0, _pos);
        BufferPool.Return(_buffer);
        _buffer = next;
    }
}
