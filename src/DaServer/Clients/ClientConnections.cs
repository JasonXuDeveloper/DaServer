namespace DaServer.Clients;

/// <summary>
/// Client connections (ADR-048): game clients, bots and tools connect to this node (a client-facing node, the
/// deployment's "gateway"), get a session, and their <see cref="FromClientAttribute">[FromClient]</see> messages enter the cluster through
/// the node's ingress with the session as origin. Messages to sessions and client groups are pushed to the sockets
/// (a group message is serialized once and fanned out here); session operations (<see cref="JoinGroup"/>,
/// <see cref="LeaveGroup"/>, <see cref="Disconnect"/>, bindings) are applied here; resume tickets let a client
/// resume on any client-facing node. Payloads are never deserialized on the way in.
/// </summary>
public sealed class ClientConnections(ClientConnectionOptions options) : NodeFeature
{
    private Node _node = null!;
    private volatile bool _stopped, _crashed;
    private TimerWheel<PendingClose> _graces = null!; // lost connections, under its lock; on the node's clock
    private SendFlusher? _flusher; // with FlushInterval: writes the sessions on its beat

    /// <summary>A lost connection whose actors learn it at the end of the resume grace (unless the client resumed).</summary>
    private readonly record struct PendingClose(ClientSession Session, SessionGrant[] Grants);

    private readonly ConcurrentDictionary<long, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<long, GroupMembers> _groups = new();
    private readonly Lock _groupGate = new(); // membership changes (control path); fan-out takes only its group's lock
    private readonly Stack<GroupMembers> _idleGroups = new(); // emptied groups for the next new ones (under the gate)
    private const int MaxIdleGroups = 4096;
    private Listener? _listener;
    private SessionHandler? _sessionHandler;
    private byte[]? _ticketSecret; // resume tickets: null when the cluster has no secret

    /// <summary>The port clients connect to (after the node started).</summary>
    public int Port { get; private set; }

    /// <summary>Connected client sessions.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>The ids of the sessions now (tools and tests).</summary>
    internal long[] SessionIds() => [.. _sessions.Keys];

    protected internal override void Start(INodeContext context)
    {
        _node = (Node)context; // a capability of this assembly: it may use the node's internals, never the other way round
        _sessionHandler = new SessionHandler(this);
        _graces = new TimerWheel<PendingClose>(TimeSpan.FromMilliseconds(100), time: _node.Options.TimeProvider);
        var addresses = new ClientAddresses(this);
        context.HandleAddress<ClientSession>(addresses);
        context.HandleAddress<Origin>(addresses); // the origin of a client's message is its session
        context.HandleAddress<ClientGroup>(addresses);
        if (_node.Options.SigningSecret is { Length: > 0 } secret) _ticketSecret = System.Text.Encoding.UTF8.GetBytes(secret);
        var sessionOptions = ConnectionOptions.ForSessions().FlushedEvery(options.FlushInterval);
        sessionOptions.MaxFrameSize = options.MaxFrameSize; // what an untrusted client may make us buffer
        _flusher = sessionOptions.Flusher;
        _listener = new Listener(new IPEndPoint(IPAddress.Parse(_node.Options.ListenAddress), options.Port), socket =>
        {
            var connection = new Connection(socket, _sessionHandler, sessionOptions);
            Open(connection);
            return connection;
        });
        _listener.Start();
        Port = _listener.LocalEndPoint.Port;
        context.AddGauge("daserver.clients.sessions", "Connected client sessions", () => _sessions.Count);
    }

    /// <summary>
    /// The node is leaving: accept no more clients (readiness already turned the load balancer away), then retire the
    /// sessions in waves over <see cref="ClientConnectionOptions.DrainSpread"/>. Each client resumes its session on
    /// another node through its endpoints before it leaves this one (ADR-026), so nothing is lost and its actors see no
    /// close; spreading the waves keeps the other nodes from receiving every client at once. Clients that have not moved
    /// within <see cref="ClientConnectionOptions.RetireGrace"/> are closed; the stop budget cuts it all short.
    /// </summary>
    protected internal override async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        var sessions = _sessions.Values.ToArray(); // once per stop
        int waves = Math.Clamp(sessions.Length / 100, 1, 20);
        var pause = options.DrainSpread / waves;
        for (int wave = 0; wave < waves && !cancellationToken.IsCancellationRequested; wave++)
        {
            long now = Platform.Milliseconds;
            for (int i = wave; i < sessions.Length; i += waves) Retire(sessions[i], now);
            if (wave < waves - 1)
                await Task.Delay(pause, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // cut short: no throw
        }

        long until = Platform.Milliseconds + (long)options.RetireGrace.TotalMilliseconds;
        while (!_sessions.IsEmpty && Platform.Milliseconds < until && !cancellationToken.IsCancellationRequested)
            await Task.Delay(100, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Close();
    }

    /// <summary>
    /// Ask <paramref name="session"/>'s client to move (<see cref="ControlOp.Retire"/>) and close its connection if it
    /// has not left within <see cref="ClientConnectionOptions.RetireGrace"/>; a session without grants has nothing to
    /// resume, so it is closed at once (its client connects again).
    /// </summary>
    private void Retire(SessionState session, long now)
    {
        session.RetireAtMs = 0;
        if (_ticketSecret == null || session.Grants.Length == 0)
        {
            session.Channel.Close();
            return;
        }

        if (session.CloseAtMs != 0) return; // asked already
        session.CloseAtMs = now + (long)options.RetireGrace.TotalMilliseconds;
        session.Channel.SendControl(ControlOp.Retire);
    }

    protected internal override void Stop(bool crashed)
    {
        _crashed |= crashed; // a crashed node tells nobody
        _stopped = true;
        Close();
    }

    private void Close()
    {
        _listener?.Dispose();
        _flusher?.Flush(); // the sessions' last frames, before they close
        foreach (var session in _sessions.Values) session.Channel.Close();
    }

    /// <summary>Messages to the sessions, origins and client groups of this node.</summary>
    private sealed class ClientAddresses(ClientConnections node) : IAddressHandler
    {
        public void OnMessage(in FrameHeader header, ReadOnlySpan<byte> frame, IReplySink? replyTo) =>
            node.OnClientAddress(header.Target, header.Key, in header, frame, replyTo);
    }

    /// <summary>
    /// A message to a session or client group of this node (from a link or sent here): delivered to the clients, or
    /// applied when it is a session operation (answered to <paramref name="replyTo"/> when asked).
    /// </summary>
    private void OnClientAddress(uint scope, long value, in FrameHeader header, ReadOnlySpan<byte> frame, IReplySink? replyTo)
    {
        bool operation = SessionOperations.Contains(header.MessageId);
        if (scope == AddressScopes.Group)
        {
            if (operation) ApplyToGroup(value, in header, frame);
            else DeliverToGroup(value, frame);
            return;
        }

        if (!operation)
        {
            DeliverToSession(value, frame); // a session, or the origin of a client's message: the same here
            return;
        }

        var status = _sessions.TryGetValue(value, out var session) ? Apply(session, in header, frame) : RpcStatus.Disconnected;
        if (replyTo == null || !header.IsRequest) return;
        uint callId = FrameHeader.CallIdOf(in header, frame);
        if (status == RpcStatus.Ok) replyTo.SendOk(callId);
        else replyTo.SendError(callId, status);
    }

    /// <summary>Messages to a client address that its node applies instead of delivering them to the client.</summary>
    private static class SessionOperations
    {
        public static readonly uint Access = TypeId<SessionAccess>.Value, Join = TypeId<JoinGroup>.Value,
            Leave = TypeId<LeaveGroup>.Value, Disconnect = TypeId<Disconnect>.Value;

        public static bool Contains(uint messageId) =>
            messageId == Join || messageId == Leave || messageId == Access || messageId == Disconnect;
    }

    private RpcStatus Apply(SessionState session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var payload = FrameHeader.PayloadOf(in header, frame);
        uint id = header.MessageId;
        if (id == SessionOperations.Join)
        {
            JoinGroup join = NinoDeserializer.Deserialize<JoinGroup>(payload);
            lock (_groupGate) Join(session, join.Group.Id);
        }
        else if (id == SessionOperations.Leave)
        {
            LeaveGroup leave = NinoDeserializer.Deserialize<LeaveGroup>(payload);
            lock (_groupGate) Leave(session, leave.Group.Id);
        }
        else if (id == SessionOperations.Disconnect)
        {
            session.Explicit = true;
            session.Channel.Close();
        }
        else
        {
            SessionAccess access = NinoDeserializer.Deserialize<SessionAccess>(payload);
            var status = ChangeAccess(session, in access);
            if (status != RpcStatus.Ok) return status;
            IssueTicket(session);
        }

        return RpcStatus.Ok;
    }

    /// <summary>
    /// Apply a grant change. A granted session also joins the actor's group (so <c>Cluster.Send(actor.Group, ..)</c>
    /// reaches it) and leaves it with the grant.
    /// </summary>
    private RpcStatus ChangeAccess(SessionState session, in SessionAccess access)
    {
        uint type = access.HandlerType;
        long key = access.Key;
        if (key == 0) return RpcStatus.Error;
        lock (_groupGate)
        {
            switch (access.Change)
            {
                case AccessChange.Bind:
                    long previous = session.KeyOf(type);
                    if (previous == key) break;
                    if (previous != 0 && session.Remove(type, previous)) Leave(session, ClientGroup.Of(type, previous).Id);
                    if (!session.Grant(type, key, bound: true)) return RpcStatus.Overloaded;
                    Join(session, ClientGroup.Of(type, key).Id);
                    break;
                case AccessChange.Grant:
                    if (!session.Grant(type, key, bound: false)) return RpcStatus.Overloaded;
                    Join(session, ClientGroup.Of(type, key).Id);
                    break;
                case AccessChange.Unbind or AccessChange.Revoke:
                    if (session.Remove(type, key)) Leave(session, ClientGroup.Of(type, key).Id);
                    break;
                default:
                    return RpcStatus.Error;
            }
        }

        return RpcStatus.Ok;
    }

    /// <summary>A session operation sent to a group: applied to each member of this node (a snapshot: it may change them).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ApplyToGroup(long groupId, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        if (!_groups.TryGetValue(groupId, out var group)) return;
        foreach (var session in group.Snapshot()) Apply(session, in header, frame);
    }

    private void DeliverToSession(long sessionId, ReadOnlySpan<byte> frame)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            session.Channel.Send(frame);
    }

    /// <summary>Fan a frame out to every local session of a group (group 0 = every session). memcpy per session.</summary>
    private void DeliverToGroup(long groupId, ReadOnlySpan<byte> frame)
    {
        if (_groups.TryGetValue(groupId, out var group)) group.Send(groupId, frame);
    }

    /// <summary>Add a session to a group (once). Caller holds the gate.</summary>
    private void Join(SessionState session, long groupId)
    {
        if (session.Closed || session.Groups.ContainsKey(groupId)) return;
        if (!_groups.TryGetValue(groupId, out var group))
            _groups[groupId] = group = (_idleGroups.TryPop(out var idle) ? idle : new GroupMembers()).For(groupId);
        group.Add(session);
    }

    /// <summary>Remove a session from a group; empty groups are dropped (bounded memory). Caller holds the gate.</summary>
    private void Leave(SessionState session, long groupId)
    {
        if (!session.Groups.ContainsKey(groupId) || !_groups.TryGetValue(groupId, out var group)) return;
        if (group.Remove(session) == 0) group.EmptySince = Environment.TickCount64; // removed later if it stays empty
    }

    /// <summary>Drop the session from its groups and tell every actor it was bound to (<see cref="SessionClosed"/>).</summary>
    private void OnSessionClosed(SessionState session)
    {
        _sessions.TryRemove(session.Id, out _);
        if (_crashed) return; // a crashed node tells nobody
        SessionGrant[] grants;
        lock (_groupGate)
        {
            session.Closed = true;
            grants = session.Grants; // final: grants change under the gate, never on a closed session
            while (session.Groups.Count > 0)
            {
                using var groups = session.Groups.Keys.GetEnumerator();
                groups.MoveNext();
                Leave(session, groups.Current);
            }
        }

        var sessionRef = new ClientSession(_node.Id, session.Id);
        if (grants.Length == 0) return;
        if (session.Explicit || ResumeGrace == TimeSpan.Zero) ReportClosed(sessionRef, grants);
        else
            lock (_graces) _graces.Schedule(ResumeGrace, new PendingClose(sessionRef, grants));
    }

    /// <summary>Report the lost connections whose grace ended, drop long-empty groups, close silent and aged connections.</summary>
    protected internal override void Maintain(long nowMs, bool late)
    {
        if (_stopped) return;
        var expired = new GraceExpired(this);
        lock (_graces) _graces.Advance(ref expired);
        if (nowMs >= _nextGroupSweep) SweepGroups();
        if (nowMs >= _nextSilenceSweep) CloseSilentSessions(nowMs, late);
    }

    /// <summary>
    /// Once a second: close the connections of clients that sent nothing for <see cref="ClientConnectionOptions.IdleTimeout"/>
    /// (two comparisons per session; idle clients ping, so only lost ones go quiet), retire those older than their
    /// <see cref="ClientConnectionOptions.MaxConnectionAge"/> (their clients move through the load balancer, so after a
    /// scale-out connections spread over the new nodes too), and close retired ones whose clients did not move.
    /// </summary>
    private void CloseSilentSessions(long now, bool late)
    {
        _nextSilenceSweep = now + 1000;
        if (late) return;
        long timeout = (long)options.IdleTimeout.TotalMilliseconds;
        foreach (var (_, session) in _sessions)
        {
            if (session.CloseAtMs != 0 && now >= session.CloseAtMs) session.Channel.Close();
            else if (session.RetireAtMs != 0 && now >= session.RetireAtMs) Retire(session, now);
            else KeepAlive.Check(session.Channel, now, 0, timeout);
        }
    }

    private long _nextSilenceSweep;

    /// <summary>
    /// Empty groups stay in the table a while (a group emptied and joined again, as area-of-interest cells are, costs
    /// nothing then); those empty for <see cref="EmptyGroupLifetimeMs"/> leave it for the pool. Bounds memory.
    /// </summary>
    private void SweepGroups()
    {
        long now = Environment.TickCount64;
        _nextGroupSweep = now + EmptyGroupLifetimeMs;
        lock (_groupGate)
            foreach (var (id, group) in _groups)
            {
                if (group.Count != 0 || now - group.EmptySince < EmptyGroupLifetimeMs || id == ClientGroup.All.Id) continue;
                _groups.TryRemove(id, out _);
                if (_idleGroups.Count < MaxIdleGroups && group.Capacity <= GroupMembers.MaxPooledMembers) _idleGroups.Push(group);
            }
    }

    private const long EmptyGroupLifetimeMs = 10_000;
    private long _nextGroupSweep;

    private readonly struct GraceExpired(ClientConnections clients) : ITimerHandler<PendingClose>
    {
        public void OnTimer(in PendingClose close)
        {
            if (!clients._stopped) clients.ReportClosed(close.Session, close.Grants);
        }
    }

    private void ReportClosed(ClientSession session, SessionGrant[] grants)
    {
        foreach (var grant in grants)
            _node.SendTo(grant.HandlerTypeId, grant.Key, new SessionClosed(session));
    }

    private TimeSpan ResumeGrace => _node.Options.EffectiveResumeGrace;

    /// <summary>Send the session's client a fresh resume ticket (after grant changes, and when the client asks).</summary>
    private void IssueTicket(SessionState session)
    {
        var grants = session.Grants;
        if (_ticketSecret == null) return;
        int size = grants.Length == 0 ? 0 : ResumeTicket.SizeOf(grants.Length); // empty: nothing to resume
        Span<byte> ticket = stackalloc byte[size];
        if (size > 0)
            ResumeTicket.Write(ticket, _ticketSecret, new ClientSession(_node.Id, session.Id), grants,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)options.TicketLifetime.TotalMilliseconds);
        var w = FrameWriter.Rent(FrameHeader.Size + size);
        w.WriteRaw(ticket);
        w.SendControl((IFrameSender)session.Sink, ControlOp.Ticket);
    }

    /// <summary>
    /// A client that lost its connection presents its ticket on a new one. The ticket only names what the session held:
    /// each actor in it is asked (<see cref="SessionResumed"/>) and accepts only while it still holds the previous session
    /// granted, so a withdrawn grant, a closed session or a ticket used once already resumes nothing. The new session
    /// takes over what was accepted (and the actors' groups), then the client gets its reply: its next messages reach
    /// actors that already know the new session. Cold path (a resume): it allocates.
    /// </summary>
    private void ResumeSession(SessionState session, uint callId, ReadOnlySpan<byte> ticket)
    {
        if (_ticketSecret == null || session.Grants.Length != 0 ||
            !ResumeTicket.TryRead(ticket, _ticketSecret, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var previous, out var granted) ||
            Interlocked.Exchange(ref session.Resuming, 1) != 0)
        {
            session.Sink.SendError(callId, RpcStatus.Forbidden);
            return;
        }

        var grants = new SessionGrant[ResumeTicket.GrantCount(granted)];
        for (int i = 0; i < grants.Length; i++) grants[i] = ResumeTicket.GrantAt(granted, i);
        _ = ResumeAsync(session, callId, previous, grants);
    }

    private async Task ResumeAsync(SessionState session, uint callId, ClientSession previous, SessionGrant[] grants)
    {
        var current = new ClientSession(_node.Id, session.Id);
        var answers = new Task<bool>[grants.Length];
        for (int i = 0; i < grants.Length; i++) answers[i] = Accepts(grants[i], new SessionResumed(current, previous));
        var accepted = await Task.WhenAll(answers).ConfigureAwait(false);

        int taken = 0;
        bool closed;
        lock (_groupGate)
        {
            closed = session.Closed;
            for (int i = 0; i < grants.Length && !closed; i++)
            {
                if (!accepted[i]) continue;
                session.Grant(grants[i].HandlerTypeId, grants[i].Key, grants[i].Bound);
                Join(session, ClientGroup.Of(grants[i].HandlerTypeId, grants[i].Key).Id);
                taken++;
            }
        }

        if (closed)
        {
            // Closed while the actors were asked: those that took the new session learn it ended.
            for (int i = 0; i < grants.Length; i++)
                if (accepted[i]) _node.SendTo(grants[i].HandlerTypeId, grants[i].Key, new SessionClosed(current));
            return;
        }

        if (taken == 0)
        {
            session.Sink.SendError(callId, RpcStatus.Forbidden);
            return;
        }

        IssueTicket(session);
        session.Sink.SendOk(callId);
    }

    private async Task<bool> Accepts(SessionGrant grant, SessionResumed resumed)
    {
        try
        {
            await _node.Resume(grant.HandlerTypeId, grant.Key, in resumed).ConfigureAwait(false);
            return true;
        }
        catch (RpcException)
        {
            return false; // refused, or the actor's node is gone: the client asks it again (logs in, enters again)
        }
    }

    // ------------------------------------------------------------------ client connections

    /// <summary>
    /// A client connected over another transport (a WebSocket of the HTTP plane): it gets a session like a TCP client.
    /// The channel's frames go to <see cref="Channels"/>.
    /// </summary>
    public void Open(IFrameChannel channel)
    {
        var session = new SessionState(((INodeContext)_node).NewOriginId(), channel) // a session is the origin of its messages
        {
            Limit = new TokenBucket(options.MessagesPerSecond, Math.Max(1, options.Burst)),
        };
        channel.Tag = session;
        if (options.MaxConnectionAge > TimeSpan.Zero) // jittered by up to a tenth, so connections opened together do not close together
            session.RetireAtMs = Platform.Milliseconds + (long)(options.MaxConnectionAge.TotalMilliseconds * (0.9 + Platform.NextDouble() * 0.2));
        _sessions[session.Id] = session; // it joins ClientGroup.All once its hello is answered
    }

    /// <summary>Largest frame a client may send (<see cref="ClientConnectionOptions.MaxFrameSize"/>).</summary>
    public int MaxFrameSize => options.MaxFrameSize;

    /// <summary>Bytes a client's channel may fall behind before it is closed (a slow consumer, like on TCP).</summary>
    public int MaxPendingSendBytes { get; } = ConnectionOptions.ForSessions().MaxPendingSendBytes;

    /// <summary>Receives the frames of channels opened with <see cref="Open"/>.</summary>
    public IChannelHandler Channels => _sessionHandler!;

    /// <summary>The frames of every client channel, TCP or not.</summary>
    private sealed class SessionHandler(ClientConnections node) : IChannelHandler
    {
        public void OnFrame(IFrameChannel channel, in FrameHeader header, ReadOnlySpan<byte> frame)
        {
            var session = (SessionState)channel.Tag!;
            // Keep-alive frames are the transport's, of every version: before the hello too (a client pings an idle
            // connection whose hello is still waiting).
            if (!session.Introduced && !(header.Kind == FrameKind.Control && header.MessageId is ControlOp.Ping or ControlOp.Pong))
            {
                node.Introduce(session, in header, frame);
                return;
            }

            switch (header.Kind)
            {
                case FrameKind.Message:
                    node.OnClientMessage(session, in header, frame);
                    break;
                case FrameKind.Control:
                    node.OnClientControl(session, in header, frame);
                    break;
                default:
                    channel.Close(new InvalidOperationException("Unexpected frame kind from client: " + header.Kind));
                    break;
            }
        }

        public void OnClosed(IFrameChannel channel, Exception? error)
        {
            if (channel.Tag is SessionState session)
                node.OnSessionClosed(session);
        }
    }

    /// <summary>
    /// The first frame of a client connection must be its <see cref="ControlOp.ClientHello"/>: a client of another
    /// protocol version is told both versions and disconnects (anything it sends after closes the connection), instead
    /// of misreading what follows. Only an introduced session is a member of <see cref="ClientGroup.All"/>: broadcasts
    /// never reach a client that would misread them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Introduce(SessionState session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint callId = FrameHeader.CallIdOf(in header, frame);
        if (session.Refused || header.Kind != FrameKind.Control || header.MessageId != ControlOp.ClientHello || callId == 0)
        {
            session.Channel.Close(new InvalidOperationException("A client connection starts with its hello, of this node's protocol"));
            return;
        }

        if (header.Key != ControlOp.ClientVersion)
        {
            session.Refused = true;
            session.Sink.SendError(callId, RpcStatus.Error,
                $"Client protocol {header.Key}, this node speaks {ControlOp.ClientVersion}: update the client");
            return; // closing now could drop the answer before it is written
        }

        session.Introduced = true;
        lock (_groupGate) Join(session, ClientGroup.All.Id); // group 0 is a group like any other
        session.Sink.SendOk(callId);
    }

    private void OnClientControl(SessionState session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint callId = FrameHeader.CallIdOf(in header, frame);
        switch (header.MessageId)
        {
            case ControlOp.Ticket:
                IssueTicket(session);
                break;
            case ControlOp.ResumeSession when callId != 0:
                ResumeSession(session, callId, FrameHeader.PayloadOf(in header, frame));
                break;
            case ControlOp.Ping:
                KeepAlive.Pong(session.Channel);
                break;
            case ControlOp.Pong:
                break;
            case ControlOp.Goodbye:
                session.Explicit = true;
                if (callId != 0) session.Sink.SendOk(callId);
                break;
            default:
                session.Channel.Close(new InvalidOperationException("Unexpected control operation from client: " + header.MessageId));
                break;
        }
    }

    /// <summary>
    /// Validate and route a client message (<see cref="FromClientAttribute">[FromClient]</see> messages only) to its
    /// handler: the actor the client named (granted to the session only), else the actor of the type handling it that
    /// is bound to the session, else the service handling it. The session is the message's origin (<c>Caller</c> in the
    /// handler).
    /// </summary>
    private void OnClientMessage(SessionState session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint replyId = FrameHeader.CallIdOf(in header, frame);
        if (options.MessagesPerSecond > 0 && !session.Limit.TryTake(out var retryAfter)) // the connection's receive loop owns it
        {
            if (replyId != 0) session.Sink.SendRefusal(replyId, RpcStatus.RateLimited, retryAfter);
            return;
        }

        if (!ClientMessages.TryGet(header.MessageId, out var message))
        {
            // Unknown to this node's build: a newer client during a rolling upgrade retries elsewhere or later (V3).
            if (replyId != 0) session.Sink.SendError(replyId, RpcStatus.NotHosted);
            return;
        }

        uint named = header.Target;
        var body = FrameHeader.PayloadOf(in header, frame);
        uint handlerTypeId;
        long key;
        if (named != 0)
        {
            // The client named the actor: only one granted to its session (no actor is reached, let alone created, else).
            if (!session.IsGranted(named, header.Key))
            {
                if (replyId != 0) session.Sink.SendError(replyId, RpcStatus.Forbidden);
                return;
            }

            (handlerTypeId, key) = (named, header.Key);
        }
        else if (_node.View.TryGetRoute(message.Id, actor: true, out var route, out _))
        {
            (handlerTypeId, key) = (route.HandlerTypeId, session.KeyOf(route.HandlerTypeId));
            if (key == 0)
            {
                if (replyId != 0) session.Sink.SendError(replyId, RpcStatus.Forbidden);
                return;
            }
        }
        else
        {
            (handlerTypeId, key) = (0, 0); // a service; Request/SendMessage answer NotHosted / drop when there is none
        }

        // The payload goes on as is; the origin is this node's session, whatever the client wrote.
        IIngress ingress = _node;
        var origin = new Origin(_node.Id, session.Id);
        if (replyId == 0) ingress.Send(message.Id, handlerTypeId, key, body, origin);
        else ingress.Ask(message.Id, handlerTypeId, key, body, origin, session.Sink, replyId, header.Budget); // the client's own timeout
    }

}

/// <summary>An actor a session's client may name; <see cref="Bound"/>: also the session's actor of its type.</summary>
internal readonly record struct SessionGrant(uint HandlerTypeId, long Key, bool Bound);

/// <summary>A connected client on this node.</summary>
internal sealed class SessionState(long id, IFrameChannel channel)
{
    public long Id { get; } = id;
    public IFrameChannel Channel { get; } = channel;
    public IReplySink Sink { get; } = new ChannelSink(channel);

    /// <summary>Whether the client's hello was accepted, or refused (another protocol version); its receive loop owns both.</summary>
    public bool Introduced, Refused;

    /// <summary>When the client is asked to move for its connection's age (<see cref="ClientConnectionOptions.MaxConnectionAge"/>; 0: never).</summary>
    public long RetireAtMs;

    /// <summary>When the connection of a client asked to move is closed if it has not left (0: not asked).</summary>
    public long CloseAtMs;

    /// <summary>The client's message rate limit (<see cref="ClientConnectionOptions.MessagesPerSecond"/>; its receive loop owns it).</summary>
    public TokenBucket Limit;

    /// <summary>Grants per session at most (they ride in the resume ticket and are scanned per named message).</summary>
    public const int MaxGrants = 64;

    private volatile SessionGrant[] _grants = [];

    /// <summary>
    /// The actors the client may name (<c>GrantSession</c>), bound ones included (<c>BindSession</c>): an immutable
    /// snapshot, read lock-free.
    /// </summary>
    public SessionGrant[] Grants => _grants;

    /// <summary>Groups the session belongs to and its slot in each (changed under the group gate).</summary>
    public Dictionary<long, int> Groups { get; } = new();

    /// <summary>Set under the group gate when the session closed: it joins no group afterwards.</summary>
    public bool Closed { get; set; }

    /// <summary>1 once the client presented a ticket: a session resumes at most once.</summary>
    public int Resuming;

    /// <summary>Closed on purpose (client goodbye, <c>Disconnect</c>): its actors learn it at once, no resume grace.</summary>
    public volatile bool Explicit;

    /// <summary>Key bound for <paramref name="handlerTypeId"/>, 0 when unbound. Lock-free (client message path).</summary>
    public long KeyOf(uint handlerTypeId)
    {
        var grants = _grants;
        for (int i = 0; i < grants.Length; i++)
            if (grants[i].HandlerTypeId == handlerTypeId && grants[i].Bound) return grants[i].Key;
        return 0;
    }

    /// <summary>Whether the client may name actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>). Lock-free.</summary>
    public bool IsGranted(uint handlerTypeId, long key)
    {
        var grants = _grants;
        for (int i = 0; i < grants.Length; i++)
            if (grants[i].Key == key && grants[i].HandlerTypeId == handlerTypeId) return true;
        return false;
    }

    /// <summary>
    /// Grant an actor (<paramref name="bound"/>: also bind it; an existing grant is bound then); false when the session
    /// holds <see cref="MaxGrants"/> already. Callers hold the group gate and unbind the previous actor of the
    /// type first; the copy is published to lock-free readers by the volatile write.
    /// </summary>
    public bool Grant(uint handlerTypeId, long key, bool bound)
    {
        var items = _grants;
        int i = IndexOf(items, handlerTypeId, key);
        if (i < 0)
        {
            if (items.Length == MaxGrants) return false;
            _grants = [.. items, new SessionGrant(handlerTypeId, key, bound)];
        }
        else if (bound && !items[i].Bound)
        {
            var next = (SessionGrant[])items.Clone();
            next[i] = items[i] with { Bound = true };
            _grants = next;
        }

        return true;
    }

    /// <summary>Remove the grant (and binding) of an actor; false when it had none. Callers hold the group gate.</summary>
    public bool Remove(uint handlerTypeId, long key)
    {
        var items = _grants;
        int i = IndexOf(items, handlerTypeId, key);
        if (i < 0) return false;
        _grants = [.. items.AsSpan(0, i), .. items.AsSpan(i + 1)];
        return true;
    }

    private static int IndexOf(SessionGrant[] items, uint handlerTypeId, long key)
    {
        for (int i = 0; i < items.Length; i++)
            if (items[i].Key == key && items[i].HandlerTypeId == handlerTypeId) return i;
        return -1;
    }
}

/// <summary>
/// The sessions of one client group on this node. Membership changes (under the group gate) and fan-out
/// both take the group's lock, so a fan-out reaches every session that joined before it (a Bind then a push on one
/// link: the push reaches the session). Join and leave are O(1) (slot per session, swap-remove); a broadcast copies
/// nothing and allocates nothing. Lock order: group gate, group, connection send lock; <see cref="IFrameChannel.Send"/>
/// never closes inline, so no close callback re-enters a group during a fan-out.
/// </summary>
internal sealed class GroupMembers
{
    private readonly Lock _gate = new();
    private SessionState[] _members = new SessionState[4];
    private FrameChannel[] _channels = new FrameChannel[4]; // the members' channels, slot for slot: a fan-out reads only these
    private int _count;
    private long _id;

    /// <summary>Which group this is now (instances are pooled: an empty group's object serves the next new group).</summary>
    public long Id => _id;

    /// <summary>Members now (read under the group gate).</summary>
    public int Count => _count;

    /// <summary>When the group last became empty (<see cref="Environment.TickCount64"/>; under the group gate).</summary>
    public long EmptySince;

    /// <summary>Members arrays up to this length are pooled with their group object.</summary>
    public const int MaxPooledMembers = 64;

    public int Capacity => _members.Length;

    /// <summary>Become group <paramref name="id"/> (an empty, pooled instance; under the group gate).</summary>
    public GroupMembers For(long id)
    {
        lock (_gate) _id = id;
        return this;
    }

    public void Add(SessionState session)
    {
        lock (_gate)
        {
            if (_count == _members.Length)
            {
                Array.Resize(ref _members, _count * 2);
                Array.Resize(ref _channels, _count * 2);
            }

            _members[_count] = session;
            _channels[_count] = (FrameChannel)session.Channel;
            session.Groups[_id] = _count++;
        }
    }

    /// <summary>Remove <paramref name="session"/> (a member); returns the members left.</summary>
    public int Remove(SessionState session)
    {
        lock (_gate)
        {
            session.Groups.Remove(_id, out int slot);
            var last = _members[--_count];
            _members[_count] = null!;
            _channels[_count] = null!;
            if (slot != _count)
            {
                _members[slot] = last;
                _channels[slot] = (FrameChannel)last.Channel;
                last.Groups[_id] = slot;
            }

            return _count;
        }
    }

    /// <summary>
    /// Send <paramref name="frame"/> to every member, if this is still group <paramref name="groupId"/> (a fan-out that
    /// looked the group up just before it emptied and was reused must not reach the next group's members).
    /// </summary>
    public void Send(long groupId, ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (_id != groupId) return;
            var channels = _channels;
            for (int i = 0; i < _count; i++) channels[i].Send(frame);
        }
    }

    /// <summary>The members now (a copy: for operations that change memberships, a rare path).</summary>
    public SessionState[] Snapshot()
    {
        lock (_gate) return _members.AsSpan(0, _count).ToArray();
    }
}
