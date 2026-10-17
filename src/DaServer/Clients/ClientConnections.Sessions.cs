namespace DaServer.Clients;

// Client connections: opening, the client hello, control frames and messages from clients.
public sealed partial class ClientConnections
{
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
