namespace DaServer.Client;

// The connection: opening and introducing it, which one is current, keep-alive and probes.
public sealed partial class ClusterClient
{
    /// <summary>The options of a new TCP connection: the client defaults, on the beat of <see cref="FlushInterval"/>.</summary>
    private ConnectionOptions NewConnectionOptions() => ConnectionOptions.ForClients().FlushedEvery(FlushInterval);

    /// <summary>
    /// When the client has sent or received nothing for this long it pings its node, which answers (ADR-053): the node
    /// then knows the client is there, and the client hears from the node even when nothing is pushed. Keep it below the
    /// node's <c>ClientConnectionOptions.IdleTimeout</c> (45 s by default).
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Nothing received for this long: the connection is dead (half-open TCP, a dropped NAT mapping, a switched
    /// network) and is closed, as a lost connection (<see cref="Disconnected"/>, then the reconnect).
    /// </summary>
    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Probe the connection now (call it when the app returns to the foreground or the network changed): a ping that
    /// is not answered within <paramref name="timeout"/> (5 s by default) closes the connection as lost, so the client
    /// reconnects at once instead of after <see cref="ServerTimeout"/>.
    /// </summary>
    public void CheckConnection(TimeSpan? timeout = null)
    {
        if (_connection is not { IsClosed: false } connection) return;
        Volatile.Write(ref _probed, connection);
        Volatile.Write(ref _probeDeadlineMs, Platform.After(timeout ?? TimeSpan.FromSeconds(5)));
        connection.SendControl(ControlOp.Ping);
    }

    private long _probeDeadlineMs; // 0: no probe waiting for its pong
    private IFrameChannel? _probed; // the connection the probe pinged: only its pong answers it, only it is closed

    /// <summary>Sweeper tick: ping when idle, close a silent connection (and an unanswered probe) unless the tick ran <paramref name="late"/>.</summary>
    private void CheckAlive(bool late)
    {
        if (_connection is not { } connection || Volatile.Read(ref _reconnecting) != 0) return;
        long now = Platform.Milliseconds;
        long probeDeadline = Volatile.Read(ref _probeDeadlineMs);
        if (probeDeadline != 0 && now >= probeDeadline && !late)
        {
            Volatile.Write(ref _probeDeadlineMs, 0);
            if (Volatile.Read(ref _probed) == connection) connection.Close(KeepAlive.Silent); // else a move replaced it
            return;
        }

        KeepAlive.Check(connection, now, (long)KeepAliveInterval.TotalMilliseconds, late ? long.MaxValue : (long)ServerTimeout.TotalMilliseconds);
    }

    /// <summary>
    /// A new connection through the entry addresses (or the WebSocket), introduced: the node accepted the client's
    /// protocol levels. Nothing but the hello, the resume and keep-alive frames goes on it until it is used
    /// (<see cref="Use"/>). <paramref name="watched"/>: it is the current connection from the start, so a node that
    /// never answers is noticed by the keep-alive (connecting, reconnecting); a move keeps the old one current meanwhile.
    /// </summary>
    private async ValueTask<IFrameChannel> OpenAsync(bool watched, CancellationToken cancellationToken = default)
    {
        if (Connector != null)
        {
            var scripted = Connector(this);
            if (watched) Watch(scripted);
            await IntroduceAsync(scripted).ConfigureAwait(false);
            return scripted;
        }

        if (_webSocket != null)
        {
            var socket = new System.Net.WebSockets.ClientWebSocket();
            await socket.ConnectAsync(_webSocket, cancellationToken).ConfigureAwait(false);
            var channel = new WebSocketChannel(socket, this, FrameHeader.MaxFrameSize, ConnectionOptions.ForClients().MaxPendingSendBytes);
            if (watched) Watch(channel);
            _ = channel.RunAsync();
            await IntroduceAsync(channel).ConfigureAwait(false);
            return channel;
        }

        Exception? last = null;
        for (int i = 0; i < _endpoints.Length; i++)
        {
            var endpoint = _endpoints[(int)((uint)Interlocked.Increment(ref _nextEndpoint) % (uint)_endpoints.Length)];
            try
            {
                var connection = await Connection.ConnectAsync(endpoint.Host, endpoint.Port, this, NewConnectionOptions(), cancellationToken)
                    .ConfigureAwait(false);
                if (watched) Watch(connection);
                await IntroduceAsync(connection).ConfigureAwait(false);
                return connection;
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or RpcException)
            {
                last = e; // unreachable, or refused (protocol levels that do not overlap mid-upgrade): try the next one
            }
        }

        throw last!;
    }

    /// <summary>Tests: opens the connections instead of TCP (a scripted node answers on them).</summary>
    internal Func<IChannelHandler, IFrameChannel>? Connector;

    /// <summary>
    /// The first call on every connection: the node checks the client protocol levels before anything else. Until the
    /// connection is the current one (<see cref="Use"/>) nothing else is sent on it but the session's resume and
    /// keep-alive frames.
    /// </summary>
    private async ValueTask IntroduceAsync(IFrameChannel channel)
    {
        var call = PendingCall<NoReply>.Rent(null);
        var done = call.Done;
        Transmit(channel, FrameWriter.Rent(FrameHeader.Size), FrameKind.Control, 0, ControlOp.ClientHello, ControlOp.Client.Packed, call, RpcStatus.Ok);
        try
        {
            await done.ConfigureAwait(false);
        }
        catch
        {
            channel.Close(); // refused (protocol levels that do not overlap) or lost: no use for this connection
            throw;
        }
    }

    /// <summary>Make the introduced <paramref name="channel"/> the current connection; the sweeper watches this client from now on.</summary>
    private void Use(IFrameChannel channel)
    {
        Volatile.Write(ref _probeDeadlineMs, 0); // a probe of the previous connection says nothing about this one
        Watch(channel);
        Volatile.Write(ref _introduced, channel);
    }

    /// <summary>Make <paramref name="channel"/> the current connection: the sweeper watches it (keep-alive, calls) from now on.</summary>
    private void Watch(IFrameChannel channel)
    {
        _connection = channel;
        GC.KeepAlive(Sweeper);
        if (!_disposed) Live.TryAdd(this, true);
    }
}
