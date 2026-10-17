using System.Net.WebSockets;

namespace DaServer.Net;

/// <summary>
/// Frames over a WebSocket (browsers, WebGL builds, clients behind HTTP-only proxies): each binary message carries one
/// or more whole frames. Like <see cref="Connection"/> (the same <see cref="SendQueue"/>): sends queue into a bounded
/// pooled batch drained by one send loop, woken once per send batch (a peer too far behind is closed), receives are
/// parsed in place; nothing is allocated per frame.
/// </summary>
public sealed class WebSocketChannel(WebSocket socket, IChannelHandler handler, int maxFrameSize, int maxPendingSendBytes)
    : FrameChannel(handler, new SendQueue(maxPendingSendBytes, 16 * 1024, releaseIdle: true, keepSpare: false)) // idle: no send memory
{
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

    private protected override void Abort() => socket.Abort(); // ends both loops at once; the peer sees the connection drop, as with TCP

    private async Task SendLoop()
    {
        try
        {
            while (!IsClosed)
            {
                await Queue.Signal.WaitAsync().ConfigureAwait(false);
                while (!IsClosed && Queue.TryTake(out var batch, out int length))
                {
                    try
                    {
                        await socket.SendAsync(new ReadOnlyMemory<byte>(batch, 0, length), WebSocketMessageType.Binary,
                            endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
                        KeepAlive.Touch(ref _lastSentMs);
                    }
                    finally
                    {
                        Queue.Release(batch);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            Queue.Close();
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
                        BufferPool.Grow(ref buffer, filled, buffer.Length * 2);
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(filled), CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    KeepAlive.Touch(ref _lastReceivedMs);
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
        using (BeginSendBatch()) // as on TCP: what these frames cause leaves in one write per connection
            while (!message.IsEmpty)
            {
                if (!FrameHeader.TryReadFrame(message, maxFrameSize, out var header))
                    throw new InvalidOperationException("Partial frame in a WebSocket message");
                Handler.OnFrame(this, in header, message[..header.Length]);
                message = message[header.Length..];
            }
    }
}
