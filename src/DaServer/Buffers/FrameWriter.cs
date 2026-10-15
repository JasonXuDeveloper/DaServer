using System;
using System.Runtime.CompilerServices;
using DaServer.Concurrency;
using DaServer.Protocol;
using Nino.Core;

namespace DaServer.Buffers;

/// <summary>
/// Pooled frame builder. The first <see cref="FrameHeader.Size"/> bytes are reserved for the header so a frame is
/// serialized exactly once, contiguously, and can be handed to a local mailbox (zero copy) or copied into a socket
/// batch. Implements <see cref="INinoBufferWriter"/> so generated code serializes arguments straight into it.
/// </summary>
/// <remarks>Ownership: whoever holds the instance must call <see cref="Dispose"/> exactly once.</remarks>
public sealed class FrameWriter : INinoBufferWriter, IDisposable
{
    private static readonly ObjectPool<FrameWriter> Pool = new ObjectPool<FrameWriter>(() => new FrameWriter(), 4096);

    private byte[] _buffer = Array.Empty<byte>();
    private int _pos;

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
        w.Deadline = 0;
        return w;
    }

    /// <summary>Rent a writer holding a copy of a complete received frame (header included).</summary>
    public static FrameWriter CopyOf(ReadOnlySpan<byte> frame)
    {
        var w = Pool.Rent();
        w._buffer = BufferPool.Rent(Math.Max(frame.Length, FrameHeader.Size));
        frame.CopyTo(w._buffer);
        w._pos = frame.Length;
        w.Deadline = 0;
        return w;
    }

    /// <summary>Nino writer appending to this frame. Cheap to create; create one per serialization pass.</summary>
    public Writer Nino
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new Writer(this);
    }

    /// <summary>Whole frame including header.</summary>
    public ReadOnlySpan<byte> Frame => new ReadOnlySpan<byte>(_buffer, 0, _pos);

    /// <summary>Payload after the header.</summary>
    public ReadOnlySpan<byte> Payload => new ReadOnlySpan<byte>(_buffer, FrameHeader.Size, _pos - FrameHeader.Size);

    public byte[] Buffer => _buffer;

    public int Length => _pos;

    /// <summary>Write the header (length is filled in automatically).</summary>
    public void SetHeader(FrameKind kind, uint requestId, uint messageId, long key, RpcStatus status = RpcStatus.Ok,
        byte flags = 0, byte hops = 0)
    {
        var h = new FrameHeader
        {
            Length = _pos,
            Kind = kind,
            Status = status,
            Hops = hops,
            Flags = flags,
            RequestId = requestId,
            MessageId = messageId,
            Key = key,
        };
        h.Write(_buffer);
    }

    /// <summary>Append raw bytes to the payload.</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _pos += bytes.Length;
    }

    /// <summary>Discard the payload, keep the buffer (reuse for another frame).</summary>
    public void ResetPayload() => _pos = FrameHeader.Size;

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
