namespace DaServer.Client;

/// <summary>
/// Connection of a client (game, bot, tool) to the cluster's client-facing nodes: sends <see cref="FromClientAttribute">[FromClient]</see> messages
/// (<c>client.Send(new Move(..))</c>, <c>await client.Ask(new GetInfo())</c>) and dispatches the messages servers
/// push to the [Handle] methods of registered handler objects (<see cref="Register"/>). When the connection is lost it
/// reconnects on its own and resumes its session with its resume ticket (<see cref="Reconnected"/>).
/// </summary>
public sealed class ClusterClient : IMessageSender, IConnectionHandler, IChannelHandler, IAsyncDisposable
{
    private readonly PendingCallTable _pending;
    private readonly Lock _handlersGate = new();
    // Registered handler objects with their type's shared handler table (copy on write; usually one or two, so a scan
    // beats a per-client dictionary: thousands of bot clients share one table per handler type).
    private (object Target, MessageHandlers Handlers)[] _handlers = [];
    private readonly MpmcQueue<FrameWriter>? _inbox;
    private IFrameChannel? _connection;
    private IFrameChannel? _introduced; // the connection whose hello was answered: the only one other frames go on
    private Uri? _webSocket; // connected over a WebSocket (reconnects go there again)
    private DnsEndPoint[] _endpoints = []; // the well-known addresses (reconnects start there again)
    // A random first endpoint spreads clients over the endpoints.
#if NET
    private int _nextEndpoint = Random.Shared.Next();
#else
    private int _nextEndpoint = new Random().Next();
#endif
    private byte[]? _ticket;          // latest resume ticket from the node (null: nothing to resume)
    private long _refreshTicketAtMs;  // local clock
    private int _reconnecting;
    private volatile bool _disposed;

    // One timer expires the calls of every client in the process (a bot process runs thousands of clients).
    private static readonly ConcurrentDictionary<ClusterClient, bool> Live = new();
    private static readonly Timer Sweeper = new(static _ =>
    {
        // A sweep that runs late (a GC pause, a starved machine) says nothing about the network: no silence verdicts then.
        long now = Platform.Milliseconds, last = Interlocked.Exchange(ref _lastSweepMs, now);
        bool late = last != 0 && now - last > 1000;
        foreach (var (client, _) in Live)
        {
            try
            {
                client._pending.SweepExpired();
                client.RefreshTicket();
                client.CheckAlive(late);
            }
            catch (Exception)
            {
                // One client's failure (an event handler throwing) must not stop the others' sweeps or the process.
            }
        }
    }, null, 100, 100);

    private static long _lastSweepMs;

    /// <param name="callbackQueueCapacity">
    /// 0: callbacks run on the IO thread as frames arrive (tools, tests). Otherwise frames wait, up to this many, for
    /// <see cref="ProcessCallbacks"/> on the application's own thread (a game loop, a UI thread, a client world); a client
    /// that falls further behind is disconnected as a slow consumer and resumes (its actors resend the state).
    /// </param>
    /// <param name="maxPendingCalls">
    /// Requests awaiting their reply at once (rounded up to a power of two, 16 to 2^24); one more fails at once with
    /// <see cref="RpcStatus.Overloaded"/>. Memory follows the peak actually in flight (slots come in doubling chunks), not this bound.
    /// </param>
    public ClusterClient(int callbackQueueCapacity = 0, int maxPendingCalls = 4096)
    {
        if (maxPendingCalls < 1) throw new ArgumentOutOfRangeException(nameof(maxPendingCalls));
        int bits = 4;
        while (bits < 24 && 1 << bits < maxPendingCalls) bits++;
        _pending = new PendingCallTable(bits);
        if (callbackQueueCapacity < 0) throw new ArgumentOutOfRangeException(nameof(callbackQueueCapacity));
        if (callbackQueueCapacity > 0) _inbox = new MpmcQueue<FrameWriter>(callbackQueueCapacity);
    }

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Write requests on a beat instead of at once (zero by default): what the client sends within one interval leaves
    /// in one write, from a flush thread the process's clients share (<see cref="SendFlusher"/>, ADR-070). For clients
    /// that send many requests at once (load generators, bots, server-side callers); a player's client keeps the
    /// default. Set before connecting; TCP connections only.
    /// </summary>
    public TimeSpan FlushInterval { get; set; }

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
        Volatile.Write(ref _probeDeadlineMs, Platform.Milliseconds + (long)(timeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds);
        connection.SendControl(ControlOp.Ping);
    }

    private long _probeDeadlineMs; // 0: no probe waiting for its pong

    /// <summary>Sweeper tick: ping when idle, close a silent connection (and an unanswered probe) unless the tick ran <paramref name="late"/>.</summary>
    private void CheckAlive(bool late)
    {
        if (_connection is not { } connection || Volatile.Read(ref _reconnecting) != 0) return;
        long now = Platform.Milliseconds;
        long probeDeadline = Volatile.Read(ref _probeDeadlineMs);
        if (probeDeadline != 0 && now >= probeDeadline && !late)
        {
            Volatile.Write(ref _probeDeadlineMs, 0);
            connection.Close(KeepAlive.Silent);
            return;
        }

        KeepAlive.Check(connection, now, (long)KeepAliveInterval.TotalMilliseconds, late ? long.MaxValue : (long)ServerTimeout.TotalMilliseconds);
    }

    public bool IsConnected => _connection is { IsClosed: false };

    /// <summary>Reconnect and resume the session when the connection is lost (needs a signing secret on the servers).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// How long to keep trying (exponential backoff up to 5 s between attempts). Servers wait
    /// <c>NodeOptions.ResumeGrace</c> (30 s by default) before they report the session closed.
    /// </summary>
    public TimeSpan ReconnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The connection was lost (calls in flight fail with <see cref="RpcStatus.Disconnected"/>); a reconnect may follow.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>
    /// The session resumed on a new connection: messages reach the same actors again, and they resend the full state
    /// (pushes sent while disconnected were lost). Grants and bindings are restored (with the groups of those actors);
    /// other groups are rejoined by the actors on <c>SessionResumed</c>.
    /// </summary>
    public event Action? Reconnected;

    /// <summary>Reconnecting gave up (timeout, or the ticket was refused): log in again.</summary>
    public event Action<Exception?>? ReconnectFailed;

    /// <summary>
    /// Connect to the cluster through its well-known address (the platform's load balancer, a DNS name: every resolved
    /// address is tried). The client stays on the node it reaches; on a lost connection it reconnects through the same
    /// address and resumes its session.
    /// </summary>
    public ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken = default) =>
        ConnectAsync([new DnsEndPoint(host, port)], cancellationToken);

    /// <summary>
    /// Connect over a WebSocket (<c>ws://node:8080/clients/ws</c>, <c>wss://</c> behind TLS termination): for browsers,
    /// WebGL builds and networks that pass HTTP only. The node's load balancer picks the node; the client stays there.
    /// </summary>
    public async ValueTask ConnectAsync(Uri webSocket, CancellationToken cancellationToken = default)
    {
        var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(webSocket, cancellationToken).ConfigureAwait(false);
        var channel = new WebSocketChannel(socket, this, FrameHeader.MaxFrameSize, ConnectionOptions.ForClients().MaxPendingSendBytes);
        _webSocket = webSocket;
        Attach(channel);
        _ = channel.RunAsync();
        await IntroduceAsync(channel).ConfigureAwait(false);
    }

    /// <summary>Connect through the first reachable of <paramref name="endpoints"/> (tried in turn, starting at a random one).</summary>
    public ValueTask ConnectAsync(IReadOnlyList<DnsEndPoint> endpoints, CancellationToken cancellationToken = default)
    {
        if (endpoints.Count == 0) throw new ArgumentException("No endpoint", nameof(endpoints));
        (_endpoints, _webSocket) = ([.. endpoints], null);
        return ConnectToEndpointAsync(cancellationToken);
    }

    /// <summary>
    /// The first call on every connection: the node checks the client protocol version before anything else. Until it
    /// is answered nothing else is sent on the connection (<see cref="_introduced"/>), only keep-alive frames.
    /// </summary>
    private async ValueTask IntroduceAsync(IFrameChannel channel)
    {
        var call = PendingCall<NoReply>.Rent(null);
        var done = call.Done;
        Transmit(channel, FrameWriter.Rent(FrameHeader.Size), FrameKind.Control, 0, ControlOp.ClientHello, ControlOp.ClientVersion, call, RpcStatus.Ok);
        try
        {
            await done.ConfigureAwait(false);
            if (channel == _connection) Volatile.Write(ref _introduced, channel);
        }
        catch
        {
            channel.Close(); // refused (another protocol version) or lost: no use for this connection
            throw;
        }
    }

    private async ValueTask ConnectToEndpointAsync(CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int i = 0; i < _endpoints.Length; i++)
        {
            var endpoint = _endpoints[(int)((uint)Interlocked.Increment(ref _nextEndpoint) % (uint)_endpoints.Length)];
            try
            {
                await ConnectToAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or RpcException)
            {
                last = e; // unreachable, or refused (another protocol version mid-upgrade): try the next one
            }
        }

        throw last!;
    }

    private async ValueTask ConnectToAsync(string host, int port, CancellationToken cancellationToken)
    {
        var connection = await Connection.ConnectAsync(host, port, this, NewConnectionOptions(), cancellationToken).ConfigureAwait(false);
        Attach(connection);
        await IntroduceAsync(connection).ConfigureAwait(false);
    }

    /// <summary>Make <paramref name="channel"/> the current connection; the sweeper watches this client's calls from now on.</summary>
    private void Attach(IFrameChannel channel)
    {
        _connection = channel;
        GC.KeepAlive(Sweeper);
        Live.TryAdd(this, true);
    }

    /// <summary>
    /// Register an object whose [Handle] methods receive the messages servers push to this client. Every registered
    /// object handling a message receives it, in registration order (the game's state and a measurement each subscribe
    /// to what they need).
    /// </summary>
    public void Register(object handler)
    {
        var handlers = HandlerRegistry.HandlersOf(handler.GetType());
        lock (_handlersGate) Volatile.Write(ref _handlers, [.. _handlers, (handler, handlers)]);
    }

    /// <summary>Run queued callbacks on the calling thread (only when constructed with a callbackQueueCapacity).</summary>
    public int ProcessCallbacks(int max = int.MaxValue)
    {
        if (_inbox == null) return 0;
        int n = 0;
        while (n < max && _inbox.TryDequeue(out var frame))
        {
            try
            {
                DispatchCallback(frame.Frame);
            }
            finally
            {
                frame.Dispose();
            }

            n++;
        }

        return n;
    }

    // ------------------------------------------------------------------ IMessageSender

    /// <summary>
    /// A <see cref="FromClientAttribute">[FromClient]</see> message to the actor <paramref name="address"/>, which
    /// must have granted this client's session (<c>GrantSession</c>; else <see cref="RpcStatus.Forbidden"/>). Clients
    /// address actors only (DAS008).
    /// </summary>
    public void Send<TAddr, T>(TAddr address, in T message) where TAddr : struct, IAddress where T : struct =>
        Send(MessageWriter.Write(in message), TypeId<T>.Value, ActorScope<TAddr>(), Addresses<TAddr>.Value(in address), null);

    /// <summary>A <see cref="FromClientAttribute">[FromClient]</see> message to the actor bound to this client's session, or to its service.</summary>
    public void Send<T>(in T message) where T : struct => Send(MessageWriter.Write(in message), TypeId<T>.Value, 0, 0, null);

    public Reply<TReply> Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, ActorScope<TAddr>(), Addresses<TAddr>.Value(in address));

    private static uint ActorScope<TAddr>() where TAddr : struct, IAddress =>
        Addresses<TAddr>.Kind == AddressKind.Actor ? Addresses<TAddr>.Scope : throw NotAnActor(typeof(TAddr));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException NotAnActor(Type type) => new($"A client sends to actors and services only, not to {type}");

    public Reply<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, 0, 0);

    private Reply<TReply> Ask<TRequest, TReply>(in TRequest request, uint handlerTypeId, long key) where TRequest : struct
    {
        var call = PendingCall<TReply>.Rent(null);
        var task = call.Task;
        Send(MessageWriter.Write(in request), TypeId<TRequest>.Value, handlerTypeId, key, call);
        return new Reply<TReply>(task);
    }

    /// <summary>
    /// A message (payload written) to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>), or with handler
    /// type 0 to the session's actor or service handling it; a request when <paramref name="call"/> is set. Takes
    /// ownership of <paramref name="w"/>.
    /// </summary>
    private void Send(FrameWriter w, uint messageId, uint handlerTypeId, long key, IPendingCall? call)
    {
        var budget = call != null ? FrameHeader.EncodeBudget((long)CallTimeout.TotalMilliseconds) : RpcStatus.Ok;
        // While reconnecting the new connection's session is not the resumed one yet: a message on it would reach no
        // bound actor, so it fails (or is dropped) as on a lost connection until the session is resumed.
        if (Volatile.Read(ref _reconnecting) != 0) Transmit(null, w, FrameKind.Message, handlerTypeId, messageId, key, call, budget);
        else Transmit(w, FrameKind.Message, handlerTypeId, messageId, key, call, budget);
    }

    /// <summary>A control frame to the node, answered when <paramref name="call"/> is set. Takes ownership of <paramref name="w"/>.</summary>
    private void SendControl(FrameWriter w, uint op, IPendingCall? call) => Transmit(w, FrameKind.Control, 0, op, 0, call, RpcStatus.Ok);

    /// <summary>
    /// On the current connection once its hello is answered; before that (connecting, reconnecting) a call fails with
    /// <see cref="RpcStatus.Disconnected"/> and a one-way message is dropped, as on a lost connection.
    /// </summary>
    private void Transmit(FrameWriter w, FrameKind kind, uint target, uint messageId, long key, IPendingCall? call, RpcStatus budget)
    {
        var connection = _connection;
        Transmit(connection == Volatile.Read(ref _introduced) ? connection : null, w, kind, target, messageId, key, call, budget);
    }

    private void Transmit(IFrameChannel? connection, FrameWriter w, FrameKind kind, uint target, uint messageId, long key, IPendingCall? call,
        RpcStatus budget)
    {
        try
        {
            if (connection == null || connection.IsClosed)
            {
                call?.Fail(RpcStatus.Disconnected);
                return;
            }

            uint callId = 0;
            if (call != null && !_pending.TryRegister(call, (int)CallTimeout.TotalMilliseconds, out callId))
            {
                call.Fail(RpcStatus.Overloaded);
                return;
            }

            try
            {
                if (call != null) w.SetCallId(callId);
                w.SetHeader(kind, target, messageId, key, budget);
            }
            catch (InvalidOperationException) when (call != null)
            {
                _pending.Take(callId)?.Fail(RpcStatus.Error, "The request exceeds the maximum frame size");
                return;
            }

            if (!connection.Send(w.Frame) && call != null)
                _pending.Take(callId)?.Fail(RpcStatus.Disconnected);
        }
        finally
        {
            w.Dispose();
        }
    }

    // ------------------------------------------------------------------ IConnectionHandler

    void IConnectionHandler.OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame) =>
        OnFrame(connection, in header, frame);

    void IConnectionHandler.OnClosed(Connection connection, Exception? error) => OnClosed(connection, error);

    void IChannelHandler.OnFrame(IFrameChannel channel, in FrameHeader header, ReadOnlySpan<byte> frame) =>
        OnFrame(channel, in header, frame);

    void IChannelHandler.OnClosed(IFrameChannel channel, Exception? error) => OnClosed(channel, error);

    private void OnFrame(IFrameChannel connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        switch (header.Kind)
        {
            case FrameKind.Reply when header.Status != RpcStatus.Ok && header.Key > 0: // refused at the edge, with a retry hint
                _pending.Take(FrameHeader.CallIdOf(in header, frame))?.Fail(header.Status, null, TimeSpan.FromMilliseconds(header.Key));
                break;
            case FrameKind.Reply:
                _pending.TryComplete(FrameHeader.CallIdOf(in header, frame), header.Status, FrameHeader.PayloadOf(in header, frame));
                break;
            case FrameKind.Message: // to this session or one of its groups
                if (_inbox == null) DispatchCallback(frame);
                else Enqueue(connection, frame);
                break;
            case FrameKind.Control when header.MessageId == ControlOp.Ping:
                KeepAlive.Pong(connection);
                break;
            case FrameKind.Control when header.MessageId == ControlOp.Pong:
                Volatile.Write(ref _probeDeadlineMs, 0); // a probe (or a keep-alive ping) answered
                break;
            case FrameKind.Control when header.MessageId == ControlOp.Ticket:
                var ticket = FrameHeader.PayloadOf(in header, frame);
                if (ticket.Length == 0) // no grants (before login, after logout): nothing to resume
                {
                    Volatile.Write(ref _ticket, null);
                    break;
                }

                long lifetime = SignedToken.ExpiresMs(ticket) - SignedToken.IssuedMs(ticket); // server clock: use only the span
                Volatile.Write(ref _ticket, ticket.ToArray());
                Volatile.Write(ref _refreshTicketAtMs, Platform.Milliseconds + lifetime / 2);
                break;
        }
    }

    private void OnClosed(IFrameChannel connection, Exception? error)
    {
        if (connection != _connection) return; // the first connection, left on purpose
        Volatile.Write(ref _probeDeadlineMs, 0); // a probe of this connection must not close the next one
        _pending.FailAll(RpcStatus.Disconnected);
        if (Volatile.Read(ref _reconnecting) != 0) return; // an attempt of the running reconnect failed: it retries
        Disconnected?.Invoke(error);
        if (!_disposed && AutoReconnect && Volatile.Read(ref _ticket) != null && Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0)
            _ = ReconnectAsync();
    }

    /// <summary>Connect again (through the endpoints: the cluster picks a live node) and resume the session.</summary>
    private async Task ReconnectAsync()
    {
        Exception? error = null;
        long deadline = Platform.Milliseconds + (long)ReconnectTimeout.TotalMilliseconds;
        var delay = TimeSpan.FromMilliseconds(100);
        while (!_disposed)
        {
            await Task.Delay(delay).ConfigureAwait(false); // a blip or a restarting node: give it a moment
            _connection?.Close(); // the previous attempt's, if it connected
            try
            {
                if (_webSocket != null) await ConnectAsync(_webSocket).ConfigureAwait(false);
                else await ConnectAsync(_endpoints).ConfigureAwait(false);
                await ResumeAsync().ConfigureAwait(false);
                Volatile.Write(ref _reconnecting, 0);
                Reconnected?.Invoke();
                return;
            }
            catch (RpcException e) when (e.Status == RpcStatus.Forbidden)
            {
                error = e; // the ticket was refused: retrying cannot help
                break;
            }
            catch (Exception e)
            {
                error = e;
            }

            if (Platform.Milliseconds + (long)delay.TotalMilliseconds > deadline) break;
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }

        _connection?.Close(); // a connection without a session: the application logs in again
        Volatile.Write(ref _reconnecting, 0);
        if (!_disposed) ReconnectFailed?.Invoke(error);
    }

    private ValueTask ResumeAsync()
    {
        var ticket = Volatile.Read(ref _ticket)!;
        var call = PendingCall<NoReply>.Rent(null);
        var task = call.Done;
        var w = FrameWriter.Rent(FrameHeader.Size + ticket.Length);
        w.WriteRaw(ticket);
        SendControl(w, ControlOp.ResumeSession, call);
        return task;
    }

    /// <summary>Ask for a fresh ticket at half of its lifetime (the sweeper timer, every 100 ms).</summary>
    private void RefreshTicket()
    {
        if (Volatile.Read(ref _ticket) == null || !IsConnected || Volatile.Read(ref _reconnecting) != 0) return;
        long now = Platform.Milliseconds;
        if (now < Volatile.Read(ref _refreshTicketAtMs)) return;
        Volatile.Write(ref _refreshTicketAtMs, now + 10_000); // until the new one arrives
        SendControl(FrameWriter.Rent(FrameHeader.Size), ControlOp.Ticket, null);
    }

    /// <summary>Tests: the latest resume ticket, and resuming with any ticket (a replay).</summary>
    internal byte[]? Ticket => Volatile.Read(ref _ticket);

    internal ValueTask ResumeWith(byte[] ticket)
    {
        Volatile.Write(ref _ticket, ticket);
        return ResumeAsync();
    }

    /// <summary>Tests: lose the connection as a network failure would (no goodbye).</summary>
    internal void DropConnection() => _connection?.Close(new System.IO.IOException("Connection dropped"));

    /// <summary>Queue a pushed frame for <see cref="ProcessCallbacks"/>; a full queue disconnects (slow consumer).</summary>
    private void Enqueue(IFrameChannel connection, ReadOnlySpan<byte> frame)
    {
        var copy = FrameWriter.CopyOf(frame);
        if (_inbox!.TryEnqueue(copy)) return;
        copy.Dispose();
        connection.Close(CallbacksBehind);
    }

    private static readonly InvalidOperationException CallbacksBehind =
        new("Pushes waiting for ProcessCallbacks exceeded the client's callbackQueueCapacity (slow consumer)");

    private void DispatchCallback(ReadOnlySpan<byte> frame)
    {
        var header = FrameHeader.Read(frame);
        var registered = Volatile.Read(ref _handlers);
        for (int i = 0; i < registered.Length; i++)
        {
            if (registered[i].Handlers.Of(header.MessageId) is not { } handler) continue;
            var context = default(CallContext);
            var task = handler.Dispatcher(registered[i].Target, FrameHeader.PayloadOf(in header, frame), in context);
            if (task.IsCompleted) task.GetAwaiter().GetResult();
        }
    }

    /// <summary>Close on purpose: the node reports the session closed at once (no resume grace).</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (IsConnected)
        {
            var call = PendingCall<NoReply>.Rent(null);
            var goodbye = call.Done;
            SendControl(FrameWriter.Rent(FrameHeader.Size), ControlOp.Goodbye, call);
            try
            {
                await Task.WhenAny(goodbye.AsTask(), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
            catch
            {
                // gone already, or slow: closing is all that is left
            }
        }

        Live.TryRemove(this, out _);
        _connection?.Close();
        if (_inbox != null)
            while (_inbox.TryDequeue(out var frame)) frame.Dispose();
    }
}
