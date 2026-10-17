namespace DaServer.Net;

/// <summary>
/// What a TCP <see cref="Connection"/> and a <see cref="WebSocketChannel"/> share: the bounded <see cref="SendQueue"/>
/// behind <see cref="Send"/>, the close (once, the handler told), the owner's tag and the activity stamps. A transport
/// adds its loops: how the writer is woken (<see cref="Wake"/>) and how the transport is torn down (<see cref="Abort"/>).
/// </summary>
public abstract class FrameChannel : IFrameChannel
{
    private static readonly InvalidOperationException SlowConsumer =
        new("Send backlog exceeded the channel's bound (slow consumer)");

    private protected readonly SendQueue Queue;
    private protected readonly IChannelHandler Handler;
    private int _closed;
    private protected long _lastReceivedMs = Platform.Milliseconds, _lastSentMs = Platform.Milliseconds;

    private protected FrameChannel(IChannelHandler handler, SendQueue queue)
    {
        Handler = handler;
        Queue = queue;
    }

    /// <summary>Free slot for the owner (e.g. session or peer node state).</summary>
    public object? Tag { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public long LastReceivedMs => Volatile.Read(ref _lastReceivedMs);

    public long LastSentMs => Volatile.Read(ref _lastSentMs);

    /// <summary>
    /// Queue a complete frame (header included). Thread-safe, never blocks on IO.
    /// Returns false when the channel is closed or was closed because the peer is too slow.
    /// </summary>
    public bool Send(ReadOnlySpan<byte> frame)
    {
        if (IsClosed) return false;
        switch (Queue.Append(frame))
        {
            case SendQueue.Result.Wake:
                Wake();
                return true;
            case SendQueue.Result.Queued:
                return true;
            case SendQueue.Result.Overflow:
                // Closed off the caller's thread: a fan-out holding a group's lock must not run the close callback inline.
                Platform.Queue(static c => c.Close(SlowConsumer), this);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Wake the writer for bytes queued into an empty queue: at once, or once when the thread's send batch ends.</summary>
    private protected virtual void Wake() => Queue.Wake();

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Abort();
        Queue.Signal.Set();
        try
        {
            Handler.OnClosed(this, error);
        }
        catch
        {
            // handler errors must not escape the IO loops
        }
    }

    /// <summary>Tear the transport down (ends both loops); runs once, on <see cref="Close"/>.</summary>
    private protected abstract void Abort();

    /// <summary>A loop ended: no frame is accepted any more (its close runs or ran).</summary>
    private protected void MarkClosed() => Volatile.Write(ref _closed, 1);

    /// <summary>
    /// Until the returned scope is disposed, sends from this thread only queue bytes; each channel's writer is woken
    /// once at the end (<see cref="SendQueue.BeginBatch"/>).
    /// </summary>
    internal static SendQueue.Batch BeginSendBatch() => SendQueue.BeginBatch();
}
