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
