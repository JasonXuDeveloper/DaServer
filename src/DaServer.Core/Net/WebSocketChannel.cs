using System.Net.WebSockets;

namespace DaServer.Net;

/// <summary>A two-way stream of whole frames: a TCP <see cref="Connection"/> or a <see cref="WebSocketChannel"/>.</summary>
public interface IFrameChannel
{
    /// <summary>Queue a complete frame (thread-safe, never blocks); false when closed or too far behind.</summary>
    bool Send(ReadOnlySpan<byte> frame);

    void Close(Exception? error = null);

    bool IsClosed { get; }

    /// <summary>Free slot for the owner (a session).</summary>
    object? Tag { get; set; }

    /// <summary>When something last arrived (<see cref="Platform.Milliseconds"/>; at least once per read).</summary>
    long LastReceivedMs { get; }

    /// <summary>When the send loop last wrote to the transport (<see cref="Platform.Milliseconds"/>).</summary>
    long LastSentMs { get; }
}

/// <summary>Receives the frames of an <see cref="IFrameChannel"/> that is not a TCP connection.</summary>
public interface IChannelHandler
{
    /// <summary>A complete frame, on the channel's receive loop; valid during the call only. Must not block.</summary>
    void OnFrame(IFrameChannel channel, in FrameHeader header, ReadOnlySpan<byte> frame);

    /// <summary>Called exactly once when the channel closes.</summary>
    void OnClosed(IFrameChannel channel, Exception? error);
}

/// <summary>
/// Frames over a WebSocket (browsers, WebGL builds, clients behind HTTP-only proxies): each binary message carries one
/// or more whole frames. Like <see cref="Connection"/>: sends queue into a bounded pooled batch drained by one send
/// loop (a peer too far behind is closed), receives are parsed in place; nothing is allocated per frame.
/// </summary>
public sealed class WebSocketChannel(WebSocket socket, IChannelHandler handler, int maxFrameSize, int maxPendingSendBytes)
    : IFrameChannel
{
    private static readonly InvalidOperationException SlowConsumer = new("Send backlog exceeded (slow consumer)");

    private readonly AsyncSignal _sendSignal = new();
    private readonly Lock _sendLock = new();
    private long _lastReceivedMs = Platform.Milliseconds;
    private long _lastSentMs = Platform.Milliseconds;

    public long LastReceivedMs => Volatile.Read(ref _lastReceivedMs);

    public long LastSentMs => Volatile.Read(ref _lastSentMs);
    private byte[] _pending = [];
    private int _pendingLength;
    private int _closed;

    public object? Tag { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Run the channel until it closes (the HTTP request or the client's connection lives as long).</summary>
    public async Task RunAsync()
    {
        var sending = SendLoop();
        Exception? error = null;
        try
        {
            await ReceiveLoop().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e;
        }

        Close(error);
        await sending.ConfigureAwait(false);
    }

    public bool Send(ReadOnlySpan<byte> frame)
    {
        if (IsClosed) return false;
        bool wake, overflow = false;
        lock (_sendLock)
        {
            if (IsClosed) return false;
            int required = _pendingLength + frame.Length;
            if (required > maxPendingSendBytes)
            {
                overflow = true;
                wake = false;
            }
            else
            {
                if (required > _pending.Length)
                {
                    var grown = BufferPool.Rent(Math.Max(required, 16 * 1024));
                    _pending.AsSpan(0, _pendingLength).CopyTo(grown);
                    BufferPool.Return(_pending);
                    _pending = grown;
                }

                frame.CopyTo(_pending.AsSpan(_pendingLength));
                wake = _pendingLength == 0;
                _pendingLength = required;
            }
        }

        if (overflow)
        {
            Platform.Queue(static c => c.Close(SlowConsumer), this);
            return false;
        }

        if (wake) _sendSignal.Set();
        return true;
    }

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        socket.Abort(); // ends both loops at once; the peer sees the connection drop, as with TCP
        _sendSignal.Set();
        handler.OnClosed(this, error);
    }

    private async Task SendLoop()
    {
        byte[] sending = [];
        try
        {
            while (!IsClosed)
            {
                await _sendSignal.WaitAsync().ConfigureAwait(false);
                while (true)
                {
                    int length;
                    lock (_sendLock)
                    {
                        length = _pendingLength;
                        if (length == 0) break;
                        (sending, _pending) = (_pending, sending); // producers fill the other buffer meanwhile
                        _pendingLength = 0;
                    }

                    await socket.SendAsync(new ReadOnlyMemory<byte>(sending, 0, length), WebSocketMessageType.Binary,
                        endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
                    Volatile.Write(ref _lastSentMs, Platform.Milliseconds);
                }

                // Idle: hold no send memory (the next send rents a buffer).
                byte[] spare = [];
                lock (_sendLock)
                    if (_pendingLength == 0)
                        (spare, _pending) = (_pending, []);
                BufferPool.Return(spare);
                BufferPool.Return(sending);
                sending = [];
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            BufferPool.Return(sending);
            lock (_sendLock)
            {
                BufferPool.Return(_pending);
                _pending = [];
                _pendingLength = 0;
            }
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = BufferPool.Rent(16 * 1024);
        try
        {
            while (true)
            {
                // One message: read it whole (frames never span messages), bounded by what a peer may send at once.
                int filled = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (filled == buffer.Length)
                    {
                        if (buffer.Length >= Math.Max(maxFrameSize, maxPendingSendBytes))
                            throw new InvalidOperationException("WebSocket message too large");
                        var grown = BufferPool.Rent(buffer.Length * 2);
                        buffer.AsSpan(0, filled).CopyTo(grown);
                        BufferPool.Return(buffer);
                        buffer = grown;
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(filled), CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    Volatile.Write(ref _lastReceivedMs, Platform.Milliseconds);
                    filled += result.Count;
                } while (!result.EndOfMessage);

                Dispatch(buffer.AsSpan(0, filled));
            }
        }
        finally
        {
            BufferPool.Return(buffer);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> message)
    {
        using (Connection.BeginSendBatch()) // as on TCP: what these frames cause leaves in one write per connection
            while (!message.IsEmpty)
            {
                if (message.Length < FrameHeader.Size) throw new InvalidOperationException("Partial frame in a WebSocket message");
                var header = FrameHeader.Read(message);
                if (header.Length < FrameHeader.Size || header.Length > maxFrameSize || header.Length > message.Length)
                    throw new InvalidOperationException("Invalid frame length " + header.Length);
                handler.OnFrame(this, in header, message[..header.Length]);
                message = message[header.Length..];
            }
    }
}
