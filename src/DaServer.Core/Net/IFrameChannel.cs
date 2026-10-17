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

/// <summary>Receives the frames and the close of an <see cref="IFrameChannel"/> (a TCP connection, a WebSocket).</summary>
public interface IChannelHandler
{
    /// <summary>
    /// A complete frame (header included), on the channel's receive loop; valid during the call only (copy it to keep
    /// it). Must not block.
    /// </summary>
    void OnFrame(IFrameChannel channel, in FrameHeader header, ReadOnlySpan<byte> frame);

    /// <summary>Called exactly once when the channel closes.</summary>
    void OnClosed(IFrameChannel channel, Exception? error);
}

internal static class FrameChannels
{
    /// <summary>Send a header-only control frame (<see cref="ControlOp"/>; <paramref name="key"/> its argument).</summary>
    public static void SendControl(this IFrameChannel channel, uint op, long key = 0)
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size];
        new FrameHeader { Length = FrameHeader.Size, Kind = FrameKind.Control, MessageId = op, Key = key }.Write(frame);
        channel.Send(frame);
    }
}
