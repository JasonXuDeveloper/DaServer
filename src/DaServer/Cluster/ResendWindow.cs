using System;
using System.Threading;
using DaServer.Buffers;
using DaServer.Net;

namespace DaServer.Cluster;

/// <summary>
/// The bytes of a node link's stream the peer has not acknowledged yet (ADR-008). The connection's send loop copies
/// each batch here before writing it, so after a disconnect the link replays exactly what the peer has not processed:
/// nothing is lost and nothing runs twice.
/// <para>Stream offsets count the bytes of every frame sent through the link, across connections (the Hello that
/// opens each connection is not part of the stream). Memory: pooled chunks released as acknowledgements arrive; at
/// most <c>maxBytes</c> are kept, and a disconnect that needs older bytes cannot resume.</para>
/// <para>Thread safety: one lock. The send loop appends, the link's receive loop acknowledges and replays.</para>
/// </summary>
internal sealed class ResendWindow(int maxBytes) : ISendTap
{
    /// <summary>Chunk size, also how often the receiver acknowledges (one chunk freed per acknowledgement).</summary>
    public const int ChunkSize = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly int _maxChunks = Math.Max(2, maxBytes / ChunkSize);
    private byte[][] _chunks = new byte[8][];
    private int _head, _count;

    // Stream offsets: _base = first byte of the head chunk, _start = first unacknowledged byte, _end = next byte.
    private long _base, _start, _end;

    // Bytes the current connection sends that are not new stream bytes: its Hello, then the replayed bytes.
    private long _skip;

    /// <summary>A new connection opens with a Hello of <paramref name="helloLength"/> bytes (not part of the stream).</summary>
    public void BeginConnection(int helloLength)
    {
        lock (_gate) _skip = helloLength;
    }

    public void OnSending(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (_skip > 0)
            {
                int skipped = (int)Math.Min(_skip, bytes.Length);
                _skip -= skipped;
                bytes = bytes.Slice(skipped);
            }

            while (!bytes.IsEmpty)
            {
                int fill = _count == 0 ? ChunkSize : (int)(_end - _base - (long)(_count - 1) * ChunkSize);
                if (fill == ChunkSize)
                {
                    AddChunk();
                    fill = 0;
                }

                int n = Math.Min(bytes.Length, ChunkSize - fill);
                bytes.Slice(0, n).CopyTo(_chunks[(_head + _count - 1) % _chunks.Length].AsSpan(fill));
                bytes = bytes.Slice(n);
                _end += n;
            }
        }
    }

    /// <summary>The peer processed everything before <paramref name="offset"/>: release it.</summary>
    public void Acknowledge(long offset)
    {
        lock (_gate)
        {
            if (offset <= _start || offset > _end) return;
            _start = offset;
            while (_count > 0 && _base + ChunkSize <= _start) ReleaseHead();
        }
    }

    /// <summary>
    /// Send everything from <paramref name="offset"/> (what the peer processed) on <paramref name="connection"/>, the
    /// new connection, right after its Hello. False when those bytes are no longer kept: the stream cannot resume.
    /// </summary>
    public bool Replay(long offset, Connection connection)
    {
        lock (_gate)
        {
            if (offset < _start || offset > _end) return false;
            _start = offset;
            while (_count > 0 && _base + ChunkSize <= _start) ReleaseHead();

            // The connection's send loop passes these bytes to OnSending again: they are already here.
            _skip += _end - offset;
            for (long position = offset; position < _end;)
            {
                long relative = position - _base;
                int offsetInChunk = (int)(relative % ChunkSize);
                int n = (int)Math.Min(ChunkSize - offsetInChunk, _end - position);
                var chunk = _chunks[(_head + (int)(relative / ChunkSize)) % _chunks.Length];
                if (!connection.Send(chunk.AsSpan(offsetInChunk, n))) break; // closed again: the next connection resumes
                position += n;
            }

            return true;
        }
    }

    /// <summary>Start a new stream at offset 0 (the old one cannot resume, or the peer restarted).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            while (_count > 0) ReleaseHead();
            _head = 0;
            _base = _start = _end = _skip = 0;
        }
    }

    private void AddChunk()
    {
        if (_count == _maxChunks)
        {
            // Over budget: drop the oldest unacknowledged bytes (a disconnect now cannot resume past them).
            ReleaseHead();
            _start = Math.Max(_start, _base);
        }

        if (_count == _chunks.Length)
        {
            var grown = new byte[_chunks.Length * 2][];
            for (int i = 0; i < _count; i++) grown[i] = _chunks[(_head + i) % _chunks.Length];
            _chunks = grown;
            _head = 0;
        }

        _chunks[(_head + _count) % _chunks.Length] = BufferPool.Rent(ChunkSize);
        _count++;
    }

    private void ReleaseHead()
    {
        BufferPool.Return(_chunks[_head]);
        _chunks[_head] = null!;
        _head = (_head + 1) % _chunks.Length;
        _count--;
        _base += ChunkSize;
    }
}
