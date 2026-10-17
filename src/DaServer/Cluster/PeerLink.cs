namespace DaServer.Cluster;

/// <summary>
/// This node's link with another node (one per remote endpoint). Everything this node sends to that node travels on
/// the link's outgoing connection: requests, one-way calls, events, pushes, session control and the replies to that
/// node's requests (the peer names itself with a <see cref="ControlOp.Hello"/>), so one ordered stream carries them: a
/// reply never overtakes the Bind sent before it.
/// <para>The stream survives disconnects (ADR-008): the outgoing connection keeps unacknowledged bytes in a
/// <see cref="ResendWindow"/>; a new connection says Hello with the stream id, the peer answers
/// <see cref="ControlOp.Resume"/> with how much of the stream it processed, and the link replays the rest before
/// anything new. Calls in flight keep waiting. The stream restarts (calls in flight fail) only when the peer restarted,
/// the window no longer holds what the peer needs, the peer stays unreachable, or it is declared dead.</para>
/// <para>The link also tracks the peer's stream to us (frames on the connection the peer opened): how much this node
/// processed, acknowledged every <see cref="ResendWindow.ChunkSize"/> bytes and on the maintenance tick.</para>
/// </summary>
internal sealed class PeerLink : IFrameSink, IDisposable
{
    private const int MaxBacklogBytes = 64 * 1024 * 1024;
    private const int MaxConnectAttempts = 10;

    private readonly Node _node;
    private readonly Lock _gate = new();
    private readonly ResendWindow _window;
    private Connection? _connection;   // open stream: new frames go here
    private Connection? _handshake;    // connected, waiting for the peer's Resume
    private List<FrameWriter>? _backlog;
    private int _backlogBytes;
    private int _dropped; // frames dropped since the last report (under the gate)
    private long _droppedReportedMs = long.MinValue / 2;
    private bool _connecting;
    private bool _disposed;
    private long _streamId = NewStreamId();
    private ulong _peerId;

    // The peer's stream to us. _received is written by the inbound connection's receive loop only.
    private Connection? _inbound;
    private long _inboundStream;
    private PaddedLong _received; // on a line of its own: every frame received writes it, every send reads the link's fields
    private long _acknowledged;
    private Task _inboundTurn = Task.CompletedTask;

    public PeerLink(Node node, string host, int port)
    {
        _node = node;
        Host = host;
        Port = port;
        _window = new ResendWindow(node.Options.LinkResendWindow);
    }

    public string Host { get; }
    public int Port { get; }

    public bool IsConnected => Volatile.Read(ref _connection) is { IsClosed: false };

    /// <summary>Tests: the open outgoing connection.</summary>
    internal Connection? Connection => Volatile.Read(ref _connection);

    /// <summary>Tests: the connection the peer opened to us.</summary>
    internal Connection? Inbound => Volatile.Read(ref _inbound);

    /// <summary>Send a complete frame (copied). Thread-safe, never blocks.</summary>
    public void Send(ReadOnlySpan<byte> frame)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection != null && connection.Send(frame))
            return;

        lock (_gate)
        {
            connection = _connection;
            if (connection != null && connection.Send(frame))
                return;
            if (_disposed)
                return;

            if (_backlogBytes + frame.Length > MaxBacklogBytes)
            {
                // Reported at most every 10 s with the count: a link down under load drops thousands a second.
                _dropped++;
                long now = Platform.Milliseconds;
                if (now - _droppedReportedMs < 10_000) return;
                Log.BacklogFull(_node.Logger, Host, Port, _dropped);
                (_dropped, _droppedReportedMs) = (0, now);
                return;
            }

            (_backlog ??= new()).Add(FrameWriter.CopyOf(frame));
            _backlogBytes += frame.Length;
            StartConnecting();
        }
    }

    /// <summary>Under the gate: reconnect unless already connecting (the closed connection, if any, is resumed).</summary>
    private void StartConnecting()
    {
        if (_connecting || _disposed) return;
        _connecting = true;
        var previous = _connection;
        Volatile.Write(ref _connection, null);
        _ = ConnectLoop(previous);
    }

    private async Task ConnectLoop(Connection? previous)
    {
        // Everything the previous connection was given must be in the window before it is replayed.
        if (previous != null) await previous.SendCompletion.ConfigureAwait(false);

        var backoff = new Backoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var options = ConnectionOptions.ForNodeLinks();
                options.Tap = _window;
                var connection = await Connection.ConnectAsync(Host, Port, _node.ClusterHandler, options)
                    .ConfigureAwait(false);
                connection.Tag = this;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        connection.Close();
                        return;
                    }

                    var hello = _node.HelloFrame(_streamId);
                    _window.BeginConnection(hello.Length);
                    _handshake = connection;
                    connection.Send(hello);
                }

                return; // continues in OnResume, on the connection's receive loop
            }
            catch (Exception e)
            {
                bool giveUp;
                lock (_gate) giveUp = _disposed || attempt >= MaxConnectAttempts;
                if (giveUp)
                {
                    _node.Logger.LogWarning(e, "Link {Host}:{Port} unreachable, dropping its stream", Host, Port);
                    Restart(connecting: false);
                    return;
                }

                await Task.Delay(backoff.Next()).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The peer answered our Hello: it processed <paramref name="received"/> bytes of the stream.</summary>
    internal void OnResume(Connection connection, long received, ulong peerId)
    {
        bool failCalls = false;
        Connection? stale = null;
        lock (_gate)
        {
            if (connection != _handshake) return;
            _handshake = null;

            if (_peerId != 0 && peerId != _peerId)
            {
                // A new process at that address: what we sent the old one is gone with it. It has no record of our
                // stream id either, so the stream continues here from offset 0.
                DropStream(newId: false);
                failCalls = true;
                _node.Logger.LogInformation("Link {Host}:{Port}: peer restarted, stream restarted", Host, Port);
            }

            _peerId = peerId;
            if (!_window.Replay(received, connection))
            {
                // The window no longer holds what the peer needs: start a new stream on a new connection.
                DropStream(newId: true);
                failCalls = true;
                _node.Logger.LogWarning("Link {Host}:{Port}: stream could not resume, restarted", Host, Port);
                stale = connection; // said Hello with the old stream id
                _connecting = false;
                StartConnecting();
            }
            else
            {
                if (_backlog != null)
                {
                    foreach (var frame in _backlog)
                    {
                        connection.Send(frame.Frame);
                        frame.Dispose();
                    }

                    _backlog = null;
                    _backlogBytes = 0;
                }

                Volatile.Write(ref _connection, connection);
                _connecting = false;
            }
        }

        stale?.Close();
        if (failCalls) _node.Pending.FailOwnedBy(this, RpcStatus.Disconnected);
    }

    /// <summary>The peer processed the stream up to <paramref name="offset"/>.</summary>
    internal void OnAcknowledge(Connection connection, long offset)
    {
        if (connection == Volatile.Read(ref _connection)) _window.Acknowledge(offset);
    }

    internal void OnConnectionClosed(Connection connection, Exception? error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (connection == _handshake)
            {
                _handshake = null;
                _connecting = false;
            }
            else if (connection != _connection)
            {
                return; // the inbound connection (its state stays until the peer resumes) or an old one
            }

            _node.Logger.LogInformation("Link {Host}:{Port} closed ({Reason}), resuming", Host, Port,
                error?.Message ?? "remote close");
            if (connection == _connection) _connecting = false;
            StartConnecting();
        }
    }

    /// <summary>
    /// The peer was declared dead: unless the link is up, drop its stream and fail the calls waiting on it instead of
    /// waiting for timeouts.
    /// </summary>
    internal void OnPeerDead()
    {
        if (!IsConnected) Restart(connecting: null);
    }

    /// <summary>
    /// Drop the stream and the backlog, fail calls in flight; the next frame starts a new stream.
    /// <paramref name="connecting"/>: the new connecting state (null = unchanged).
    /// </summary>
    private void Restart(bool? connecting)
    {
        lock (_gate)
        {
            DropStream(newId: true);
            if (connecting.HasValue) _connecting = connecting.Value;
        }

        _node.Pending.FailOwnedBy(this, RpcStatus.Disconnected);
    }

    /// <summary>
    /// Under the gate: forget what was sent and queued; the caller fails the calls in flight (Disconnected: their outcome
    /// is unknown, as after a timeout). A new id makes the peer start counting again on our next Hello.
    /// </summary>
    private void DropStream(bool newId)
    {
        if (_backlog != null)
            foreach (var frame in _backlog)
                frame.Dispose();
        _backlog = null;
        _backlogBytes = 0;
        _window.Reset();
        if (newId) _streamId = NewStreamId();
    }

    // ------------------------------------------------------------------ the peer's stream to us

    /// <summary>
    /// The peer opened <paramref name="connection"/> for stream <paramref name="streamId"/>. Once the previous inbound
    /// connection has delivered its last frame, answer how much of the stream this node processed.
    /// </summary>
    internal void OnInboundHello(Connection connection, long streamId)
    {
        connection.Tag = this;
        lock (_gate) _inboundTurn = AcceptInbound(_inboundTurn, connection, streamId);
    }

    private async Task AcceptInbound(Task previousTurn, Connection connection, long streamId)
    {
        await previousTurn.ConfigureAwait(false);
        var old = Volatile.Read(ref _inbound);
        if (old != null && old != connection)
        {
            old.Close();
            await old.ReceiveCompletion.ConfigureAwait(false); // every frame it delivered is counted
        }

        long received;
        lock (_gate)
        {
            if (_inboundStream != streamId)
            {
                _inboundStream = streamId;
                Volatile.Write(ref _received.Value, 0);
            }

            received = _received.Value;
            _acknowledged = received;
            Volatile.Write(ref _inbound, connection);
        }

        var w = FrameWriter.Rent();
        w.Write(_node.Id);
        w.SendControl(connection, ControlOp.Resume, received);
    }

    /// <summary>A frame arrived on <paramref name="connection"/> (before it is handled; IO thread).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void OnReceived(Connection connection, int length)
    {
        if (connection != _inbound) return; // our own outgoing connection, or before the Resume
        long received = _received.Value + length;
        Volatile.Write(ref _received.Value, received);
        if (received - _acknowledged >= ResendWindow.ChunkSize) Acknowledge(connection, received);
    }

    /// <summary>
    /// Maintenance tick (ADR-053): ping on the outgoing connection when it has sent nothing for
    /// <paramref name="intervalMs"/> (the peer answers, as it acknowledges data), and close either connection when it
    /// has received nothing for <paramref name="timeoutMs"/>: a half-open link or a peer that stopped reading is
    /// reconnected and resumed from the resend window, while membership alone decides whether the peer is dead.
    /// </summary>
    internal void CheckAlive(long nowMs, long intervalMs, long timeoutMs)
    {
        if (Volatile.Read(ref _connection) is { } outgoing) KeepAlive.Check(outgoing, nowMs, intervalMs, timeoutMs);
        if (Volatile.Read(ref _inbound) is { } inbound) KeepAlive.Check(inbound, nowMs, 0, timeoutMs);
    }

    /// <summary>Maintenance tick: acknowledge the tail of the peer's stream.</summary>
    internal void AcknowledgeReceived()
    {
        var connection = Volatile.Read(ref _inbound);
        long received = Volatile.Read(ref _received.Value);
        if (connection != null && received != _acknowledged && !connection.IsClosed) Acknowledge(connection, received);
    }

    private void Acknowledge(Connection connection, long received)
    {
        _acknowledged = received;
        connection.SendControl(ControlOp.Acknowledge, received);
    }

    /// <summary>Tests: forget the peer's stream, as a restarted process would.</summary>
    internal void ForgetInboundStream()
    {
        lock (_gate) _inboundStream = 0;
    }

    private static long NewStreamId() => Random.Shared.NextInt64(1, long.MaxValue);

    public void Dispose()
    {
        Connection? connection, handshake;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
            handshake = _handshake;
        }

        connection?.Close();
        handshake?.Close();
        Restart(connecting: false);
    }
}
