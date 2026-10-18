namespace DaServer.Client;

/// <summary>
/// Connection of a client (game, bot, tool) to the cluster's client-facing nodes: sends <see cref="FromClientAttribute">[FromClient]</see> messages
/// (<c>client.Send(new Move(..))</c>, <c>await client.Ask(new GetInfo())</c>) and dispatches the messages servers
/// push to the [Handle] methods of registered handler objects (<see cref="Register"/>). When the connection is lost it
/// reconnects on its own and resumes its session with its resume ticket (<see cref="Reconnected"/>).
/// </summary>
public sealed partial class ClusterClient : IMessageSender, IChannelHandler, IAsyncDisposable
{
    private readonly PendingCallTable _pending;
    private readonly Lock _handlersGate = new();
    // Registered handler objects with their type's shared handler table (copy on write; usually one or two, so a scan
    // beats a per-client dictionary: thousands of bot clients share one table per handler type).
    private (object Target, MessageHandlers Handlers)[] _handlers = [];
    private readonly MpmcQueue<FrameWriter>? _inbox;

    // Pushes: one connection's at a time (callbacks never overlap), and during a move the connection left drains first.
    // Only a move takes the gate: outside one, a single receive loop delivers.
    private readonly Lock _pushGate = new();
    private IFrameChannel? _draining;                 // the connection being moved away from, while it drains
    private readonly Queue<FrameWriter> _parked = new(); // the other connections' pushes meanwhile, in order
    private const int MaxParked = 4096;
    private IFrameChannel? _connection;
    private IFrameChannel? _introduced; // the connection whose hello was answered: the only one other frames go on
    private Uri? _webSocket; // connected over a WebSocket (reconnects go there again)
    private DnsEndPoint[] _endpoints = []; // the well-known addresses (reconnects start there again)
    // A random first endpoint spreads clients over the endpoints.
    private int _nextEndpoint = (int)(Platform.NextDouble() * int.MaxValue);
    private byte[]? _ticket;          // latest resume ticket from the node (null: nothing to resume)
    private long _refreshTicketAtMs;  // local clock
    private int _reconnecting;
    private int _moving; // 1 while the session moves to a new connection on the node's request (Retire)
    private volatile bool _disposed;

    // One timer expires the calls of every client in the process (a bot process runs thousands of clients).
    private static readonly ConcurrentDictionary<ClusterClient, bool> Live = new();
    private static readonly Timer Sweeper = new(static _ =>
    {
        bool late = _sweepClock.Tick(Platform.Milliseconds) > 0;
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

    private static SilenceClock _sweepClock;

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

    public bool IsConnected => _connection is { IsClosed: false };

    /// <summary>Reconnect and resume the session when the connection is lost (needs a signing secret on the servers).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// How long to keep trying (exponential backoff with full jitter, up to 5 s between attempts). Servers wait
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

    /// <summary>
    /// The session moved to a new connection on its node's request (the connection's age, a leaving node): nothing was
    /// lost, the actors resend their state as on a resume.
    /// </summary>
    public event Action? Moved;

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
        _webSocket = webSocket;
        Use(await OpenAsync(watched: true, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Connect through the first reachable of <paramref name="endpoints"/> (tried in turn, starting at a random one).</summary>
    public async ValueTask ConnectAsync(IReadOnlyList<DnsEndPoint> endpoints, CancellationToken cancellationToken = default)
    {
        if (endpoints.Count == 0) throw new ArgumentException("No endpoint", nameof(endpoints));
        (_endpoints, _webSocket) = ([.. endpoints], null);
        Use(await OpenAsync(watched: true, cancellationToken).ConfigureAwait(false));
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

    public Reply<TReply> Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request, in CallOptions options)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, ActorScope<TAddr>(), Addresses<TAddr>.Value(in address), options.Timeout);

    private static uint ActorScope<TAddr>() where TAddr : struct, IAddress =>
        Addresses<TAddr>.Kind == AddressKind.Actor ? Addresses<TAddr>.Scope : throw NotAnActor(typeof(TAddr));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException NotAnActor(Type type) => new($"A client sends to actors and services only, not to {type}");

    public Reply<TReply> Ask<TRequest, TReply>(in TRequest request, in CallOptions options) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, 0, 0, options.Timeout);

    private Reply<TReply> Ask<TRequest, TReply>(in TRequest request, uint handlerTypeId, long key, TimeSpan timeout) where TRequest : struct
    {
        var call = PendingCall<TReply>.Rent(null);
        var task = call.Task;
        Send(MessageWriter.Write(in request), TypeId<TRequest>.Value, handlerTypeId, key, call, timeout);
        return new Reply<TReply>(task);
    }

    /// <summary>
    /// A message (payload written) to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>), or with handler
    /// type 0 to the session's actor or service handling it; a request when <paramref name="call"/> is set. Takes
    /// ownership of <paramref name="w"/>.
    /// </summary>
    private void Send(FrameWriter w, uint messageId, uint handlerTypeId, long key, IPendingCall? call, TimeSpan timeout = default)
    {
        if (timeout <= TimeSpan.Zero) timeout = CallTimeout; // CallOptions without a timeout of their own
        var budget = call != null ? FrameHeader.EncodeBudget((long)timeout.TotalMilliseconds) : RpcStatus.Ok;
        // While reconnecting the new connection's session is not the resumed one yet: a message on it would reach no
        // bound actor, so it fails (or is dropped) as on a lost connection until the session is resumed.
        var connection = Volatile.Read(ref _reconnecting) != 0 ? null : Volatile.Read(ref _introduced);
        Transmit(connection, w, FrameKind.Message, handlerTypeId, messageId, key, call, budget, timeout: timeout);
    }

    /// <summary>A control frame to the node, answered when <paramref name="call"/> is set. Takes ownership of <paramref name="w"/>.</summary>
    private void SendControl(FrameWriter w, uint op, IPendingCall? call) => Transmit(w, FrameKind.Control, 0, op, 0, call, RpcStatus.Ok);

    /// <summary>
    /// On the current connection once its hello is answered; before that (connecting, reconnecting) a call fails with
    /// <see cref="RpcStatus.Disconnected"/> and a one-way message is dropped, as on a lost connection.
    /// </summary>
    private void Transmit(FrameWriter w, FrameKind kind, uint target, uint messageId, long key, IPendingCall? call, RpcStatus budget) =>
        Transmit(Volatile.Read(ref _introduced), w, kind, target, messageId, key, call, budget);

    /// <summary>
    /// Register <paramref name="call"/> and send the frame on <paramref name="connection"/>. A frame the connection refuses
    /// (closed, or sealed by a move as this send picked it) goes on the current connection, its call registered there
    /// again: a call belongs to the connection its frame went on, so that connection's close is the call's fate.
    /// <paramref name="last"/>: the connection's last frame (<see cref="IFrameChannel.SendLast"/>), never sent elsewhere.
    /// </summary>
    private void Transmit(IFrameChannel? connection, FrameWriter w, FrameKind kind, uint target, uint messageId, long key, IPendingCall? call,
        RpcStatus budget, bool last = false, TimeSpan timeout = default)
    {
        try
        {
            if (connection == null)
            {
                call?.Fail(RpcStatus.Disconnected);
                return;
            }

            long deadline = PendingCallTable.DeadlineAfter(timeout > TimeSpan.Zero ? timeout : CallTimeout);
            uint callId = 0;
            if (call != null && !_pending.TryRegister(call, deadline, out callId, connection))
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

            while (!(last ? connection.SendLast(w.Frame) : connection.Send(w.Frame)))
            {
                var current = last ? null : Volatile.Read(ref _introduced);
                var refused = call != null ? _pending.Take(callId) : null;
                if (current == null || current == connection)
                {
                    refused?.Fail(RpcStatus.Disconnected);
                    return;
                }

                if (call != null)
                {
                    if (refused == null) return; // expired meanwhile
                    if (!_pending.TryRegister(refused, deadline, out callId, current))
                    {
                        refused.Fail(RpcStatus.Overloaded);
                        return;
                    }

                    w.SetCallId(callId); // the trailer is there: rewritten in place
                }

                connection = current;
            }
        }
        finally
        {
            w.Dispose();
        }
    }

    // ------------------------------------------------------------------ IChannelHandler

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
                Deliver(connection, frame);
                break;
            case FrameKind.Control when header.MessageId == ControlOp.Ping:
                KeepAlive.Pong(connection);
                break;
            case FrameKind.Control when header.MessageId == ControlOp.Pong:
                if (connection == Volatile.Read(ref _probed)) Volatile.Write(ref _probeDeadlineMs, 0); // the probe answered
                break;
            case FrameKind.Control when header.MessageId == ControlOp.Retire:
                // Not while the last move still drains its old connection (a second within its goodbye): the node
                // closes this one after its RetireGrace, and the client resumes then.
                if (connection == _connection && !_disposed && AutoReconnect && Volatile.Read(ref _ticket) != null &&
                    Volatile.Read(ref _draining) == null &&
                    Volatile.Read(ref _reconnecting) == 0 && Interlocked.CompareExchange(ref _moving, 1, 0) == 0)
                    _ = MoveAsync();
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

    /// <summary>
    /// A pushed frame: to the callbacks (inline, or queued for <see cref="ProcessCallbacks"/>), one connection at a time.
    /// While a move drains the connection left, pushes of the new one wait: an actor pushes to the old session, then
    /// takes the new one, so what the old connection carries was sent first.
    /// </summary>
    private void Deliver(IFrameChannel connection, ReadOnlySpan<byte> frame)
    {
        // Outside a move one connection delivers (one receive loop): no lock. A move sets _draining before its new
        // connection exists and clears it only after the parked pushes went on, both under the gate.
        if (Volatile.Read(ref _draining) == null)
        {
            Hand(connection, frame);
            return;
        }

        lock (_pushGate)
        {
            if (_draining != null && connection != _draining)
            {
                if (_parked.Count < MaxParked) _parked.Enqueue(FrameWriter.CopyOf(frame));
                else connection.Close(CallbacksBehind);
                return;
            }

            Hand(connection, frame);
        }
    }

    private void Hand(IFrameChannel connection, ReadOnlySpan<byte> frame)
    {
        if (_inbox == null) DispatchCallback(frame);
        else Enqueue(connection, frame);
    }

    /// <summary>A move starts: pushes of other connections wait until <paramref name="old"/> drained.</summary>
    private void Drain(IFrameChannel old)
    {
        lock (_pushGate) Volatile.Write(ref _draining, old);
    }

    /// <summary>
    /// The connection <paramref name="old"/> left drained (its goodbye was answered, or it closed), or the move failed (<paramref name="keep"/>
    /// false: the pushes of a session that did not take over are dropped): the waiting pushes go on, in order.
    /// </summary>
    private void Drained(IFrameChannel old, bool keep)
    {
        lock (_pushGate)
        {
            if (_draining != old) return; // a later move drains its own connection now
            while (_parked.TryDequeue(out var frame))
            {
                try
                {
                    if (keep && _connection is { } current) Hand(current, frame.Frame);
                }
                finally
                {
                    frame.Dispose();
                }
            }

            Volatile.Write(ref _draining, null); // after the parked ones: a push arriving meanwhile waited on the gate
        }
    }

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
        // On the current connection, also one still resuming (a reconnect): the node then reports the session closed
        // at once whether the resume completes or not.
        if (_connection is { IsClosed: false } connection) await GoodbyeAsync(connection).ConfigureAwait(false);

        Live.TryRemove(this, out _);
        _connection?.Close();
        if (_inbox != null)
            while (_inbox.TryDequeue(out var frame)) frame.Dispose();
    }
}
