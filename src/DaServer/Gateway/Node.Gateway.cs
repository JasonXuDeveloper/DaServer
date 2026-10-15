namespace DaServer.Runtime;

// Gateway capability (NodeOptions.ClientPort): client sessions, routing of client messages into the cluster, push and
// group broadcast fan-out. Client messages are routed by patching header fields; payloads are never deserialized here.
sealed partial class Node
{
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly ConcurrentDictionary<long, GroupMembers> _groups = new();
    private readonly Lock _groupGate = new(); // membership changes (control path); fan-out takes only its group's lock
    private Listener? _clientListener;
    private GatewayHandler? _gatewayHandler;
    private byte[]? _ticketSecret; // reconnect tickets: null when the cluster has no secret

    /// <summary>Port clients connect to (0 when this node accepts no clients).</summary>
    public int ClientPort { get; private set; }

    /// <summary>Connected client sessions on this gateway.</summary>
    public int SessionCount => _sessions.Count;

    private void StartGateway()
    {
        if (Options.ClientPort is not { } port) return;
        _gatewayHandler = new GatewayHandler(this);
        var addresses = new ClientAddresses(this);
        INodeContext context = this;
        context.HandleAddress<SessionRef>(addresses);
        context.HandleAddress<Origin>(addresses); // the origin of a client's message is its session
        if (!Options.Balancer) context.HandleAddress<ClientGroup>(addresses); // a balancer keeps no sessions
        if (Options.ClusterSecret is { Length: > 0 } secret) _ticketSecret = System.Text.Encoding.UTF8.GetBytes(secret);
        var sessionOptions = ConnectionOptions.ForClients();
        sessionOptions.MaxFrameSize = Options.MaxClientFrameSize; // what an untrusted client may make us buffer
        _clientListener = new Listener(new IPEndPoint(IPAddress.Parse(Options.ListenAddress), port), socket =>
        {
            var connection = new Connection(socket, _gatewayHandler, sessionOptions);
            var session = new Session(((INodeContext)this).NewOriginId(), connection); // a session is the origin of its messages
            connection.Tag = session;
            _sessions[session.Id] = session;
            lock (_groupGate) Join(session, ClientGroup.All.Id); // group 0 is a group like any other
            return connection;
        });
        _clientListener.Start();
        ClientPort = _clientListener.LocalEndPoint.Port;
    }

    private void StopGateway()
    {
        _clientListener?.Dispose();
        foreach (var session in _sessions.Values) session.Connection.Close();
    }

    // ------------------------------------------------------------------ ICluster: sessions (any node)

    /// <summary>
    /// Bind (or unbind) a session to an actor (<see cref="Actor.BindSession"/>): a <see cref="Bind"/> asked of the session,
    /// so it completes once the session's gateway applied it and the client's next message already reaches the actor.
    /// </summary>
    internal ValueTask BindSession(SessionRef session, uint handlerTypeId, long key)
    {
        var call = PendingCall<NoReply>.Rent(Shard.Current);
        var task = call.Done;
        AskNode(MessageWriter.Write(new Bind(handlerTypeId, key)), AddressScopes.Session, MessageType<Bind>.Id,
            session.GatewayNodeId, session.SessionId, call);
        return task;
    }

    /// <summary>
    /// A message (payload written) to something node <paramref name="node"/> holds: today a client session (by its
    /// <see cref="SessionRef"/> or the <see cref="Origin"/> of its messages). Takes
    /// ownership of <paramref name="frame"/>.
    /// </summary>
    private void SendToNode(FrameWriter frame, uint scope, uint messageId, ulong node, long value)
    {
        try
        {
            frame.SetHeader(FrameKind.Message, scope, messageId, value);
            if (node == Id) DeliverToAddress(frame.Frame, null);
            else LinkTo(node)?.Send(frame.Frame);
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// A message (payload written) to a group: serialized once, one frame per node handling the group's scope (the
    /// nodes that may have members), which fans it out locally. Takes ownership of <paramref name="frame"/>.
    /// </summary>
    private void SendToGroup(FrameWriter frame, uint scope, uint messageId, long value)
    {
        try
        {
            frame.SetHeader(FrameKind.Message, scope, messageId, value);
            foreach (var member in View.NodesWithScope(scope))
            {
                if (member.NodeId == Id) DeliverToAddress(frame.Frame, null);
                else GetLink(member).Send(frame.Frame);
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>Messages to the sessions, origins and client groups of this gateway.</summary>
    private sealed class ClientAddresses(Node node) : IAddressHandler
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
        public static readonly uint Bind = MessageType<Bind>.Id, Join = MessageType<JoinGroup>.Id,
            Leave = MessageType<LeaveGroup>.Id, Disconnect = MessageType<Disconnect>.Id;

        public static bool Contains(uint messageId) =>
            messageId == Join || messageId == Leave || messageId == Bind || messageId == Disconnect;
    }

    private RpcStatus Apply(Session session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var reader = new Reader(FrameHeader.PayloadOf(in header, frame));
        uint id = header.MessageId;
        if (id == SessionOperations.Join)
        {
            NinoDeserializer.Deserialize(out JoinGroup join, ref reader);
            lock (_groupGate) Join(session, join.Group.Id);
        }
        else if (id == SessionOperations.Leave)
        {
            NinoDeserializer.Deserialize(out LeaveGroup leave, ref reader);
            lock (_groupGate) Leave(session, leave.Group.Id);
        }
        else if (id == SessionOperations.Disconnect)
        {
            session.Explicit = true;
            session.Connection.Close();
        }
        else
        {
            NinoDeserializer.Deserialize(out Bind bind, ref reader);
            lock (_groupGate)
            {
                // A bound session also joins the actor's group, so Cluster.Send(actor.Group, ..) reaches it.
                long previous = session.Bind(bind.HandlerType, bind.Key);
                if (previous != 0 && previous != bind.Key) Leave(session, ClientGroup.Of(bind.HandlerType, previous).Id);
                if (bind.Key != 0) Join(session, ClientGroup.Of(bind.HandlerType, bind.Key).Id);
            }

            IssueTicket(session);
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
            session.Connection.Send(frame);
    }

    /// <summary>Fan a frame out to every local session of a group (group 0 = every session). memcpy per session.</summary>
    private void DeliverToGroup(long groupId, ReadOnlySpan<byte> frame)
    {
        if (_groups.TryGetValue(groupId, out var group)) group.Send(frame);
    }

    private void OnControl(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var payload = FrameHeader.PayloadOf(in header, frame);
        switch (header.MessageId)
        {
            case ControlOp.Hello:
                OnHello(connection, (ulong)header.Key, payload);
                break;
            case ControlOp.Resume:
                OnResume(connection, header.Key, payload);
                break;
            case ControlOp.Acknowledge:
                (connection.Tag as PeerLink)?.OnAcknowledge(connection, header.Key);
                break;
            case ControlOp.Gossip:
            case ControlOp.GossipReply:
            case ControlOp.CardRequest:
            case ControlOp.Cards:
                Membership.OnGossip(connection, header.MessageId, payload);
                break;

        }
    }

    /// <summary>Add a session to a group (once). Caller holds the gate.</summary>
    private void Join(Session session, long groupId)
    {
        if (session.Closed || session.Groups.ContainsKey(groupId)) return;
        if (!_groups.TryGetValue(groupId, out var group)) _groups[groupId] = group = new GroupMembers(groupId);
        group.Add(session);
    }

    /// <summary>Remove a session from a group; empty groups are dropped (bounded memory). Caller holds the gate.</summary>
    private void Leave(Session session, long groupId)
    {
        if (!session.Groups.ContainsKey(groupId) || !_groups.TryGetValue(groupId, out var group)) return;
        if (group.Remove(session) == 0) _groups.TryRemove(groupId, out _);
    }

    /// <summary>Drop the session from its groups and tell every actor it was bound to (<see cref="SessionClosed"/>).</summary>
    private void OnSessionClosed(Session session)
    {
        _sessions.TryRemove(session.Id, out _);
        if (_killed) return; // a crashed gateway tells nobody
        lock (_groupGate)
        {
            session.Closed = true;
            while (session.Groups.Count > 0)
            {
                using var groups = session.Groups.Keys.GetEnumerator();
                groups.MoveNext();
                Leave(session, groups.Current);
            }
        }

        var sessionRef = new SessionRef(Id, session.Id);
        var bindings = session.Bindings;
        if (bindings.Length == 0) return;
        if (session.Explicit || ReconnectGrace == TimeSpan.Zero) ReportClosed(sessionRef, bindings);
        else _ = ReportClosedAfterGrace(sessionRef, bindings);
    }

    /// <summary>A lost connection: the client may resume within the grace (its actors then get SessionResync instead).</summary>
    private async Task ReportClosedAfterGrace(SessionRef session, SessionBinding[] bindings)
    {
        await Task.Delay(ReconnectGrace).ConfigureAwait(false);
        if (Volatile.Read(ref _started) == 1) ReportClosed(session, bindings);
    }

    private void ReportClosed(SessionRef session, SessionBinding[] bindings)
    {
        foreach (var binding in bindings)
            SendTo(binding.HandlerTypeId, binding.Key, new SessionClosed(session));
    }

    /// <summary>How long a lost client may take to resume: <see cref="NodeOptions.ReconnectGrace"/> when tickets exist.</summary>
    private TimeSpan ReconnectGrace => Options.ClusterSecret is { Length: > 0 } ? Options.ReconnectGrace : TimeSpan.Zero;

    /// <summary>Send the session's client a fresh reconnect ticket (after binding changes, and when the client asks).</summary>
    private void IssueTicket(Session session)
    {
        var bindings = session.Bindings;
        if (_ticketSecret == null || bindings.Length > ReconnectTicket.MaxBindings) return;
        int size = ReconnectTicket.SizeOf(bindings.Length);
        Span<byte> ticket = stackalloc byte[size];
        ReconnectTicket.Write(ticket, _ticketSecret, new SessionRef(Id, session.Id), bindings,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)Options.ReconnectTicketLifetime.TotalMilliseconds);
        var w = FrameWriter.Rent(FrameHeader.Size + size);
        w.WriteRaw(ticket);
        w.SendControl(session.Connection, ControlOp.Ticket);
    }

    /// <summary>
    /// A client that lost its connection presents its ticket on a new one: the new session takes over the bindings
    /// (and the actors' groups), and every bound actor learns the new session (<see cref="SessionResync"/>) before the
    /// client's next message, which travels behind it on the same links.
    /// </summary>
    private void ResumeSession(Session session, uint callId, ReadOnlySpan<byte> ticket)
    {
        if (_ticketSecret == null || session.Bindings.Length != 0 ||
            !ReconnectTicket.TryRead(ticket, _ticketSecret, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var previous, out var bindings))
        {
            session.Sink.SendError(callId, RpcStatus.Forbidden);
            return;
        }

        int count = ReconnectTicket.BindingCount(bindings);
        for (int i = 0; i < count; i++)
        {
            var binding = ReconnectTicket.BindingAt(bindings, i);
            lock (_groupGate)
            {
                session.Bind(binding.HandlerTypeId, binding.Key);
                Join(session, ClientGroup.Of(binding.HandlerTypeId, binding.Key).Id);
            }
        }

        var current = new SessionRef(Id, session.Id);
        for (int i = 0; i < count; i++)
        {
            var binding = ReconnectTicket.BindingAt(bindings, i);
            SendTo(binding.HandlerTypeId, binding.Key, new SessionResync(current, previous));
        }

        IssueTicket(session);
        session.Sink.SendOk(callId);
    }

    /// <summary>
    /// Answer "which gateway should I use": the less loaded of two random gateways that accept sessions (power of two
    /// choices: even spread from gossiped counts, no herd on the single least loaded one while counts are stale).
    /// </summary>
    private void PickGateway(Session session, uint callId)
    {
        var up = View.UpMembers;
        ClusterMember? first = null, second = null;
        int candidates = 0;
        foreach (var member in up)
        {
            if (!member.AcceptsSessions) continue;
            candidates++;
            // Reservoir sampling of two distinct candidates in one pass.
            if (Random.Shared.Next(candidates) == 0) (first, second) = (member, first);
            else if (second == null || Random.Shared.Next(candidates - 1) == 0) second = member;
        }

        if (first == null)
        {
            session.Sink.SendError(callId, RpcStatus.NotHosted);
            return;
        }

        var pick = second != null && second.ExpectedSessions < first.ExpectedSessions ? second : first;
        Interlocked.Increment(ref pick.Directed);
        var w = FrameWriter.Rent(64);
        var nino = w.Nino;
        NinoSerializer.Serialize(new GatewayEndpoint(pick.ClientHost, pick.ClientPort), ref nino);
        session.Sink.SendReply(callId, RpcStatus.Ok, w);
    }

    // ------------------------------------------------------------------ client connections

    private sealed class GatewayHandler(Node node) : IConnectionHandler
    {
        public void OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
        {
            var session = (Session)connection.Tag!;
            switch (header.Kind)
            {
                case FrameKind.Message:
                    node.OnClientMessage(session, in header, frame);
                    break;
                case FrameKind.Control:
                    node.OnClientControl(session, in header, frame);
                    break;
                default:
                    connection.Close(new InvalidOperationException("Unexpected frame kind from client: " + header.Kind));
                    break;
            }
        }

        public void OnClosed(Connection connection, Exception? error)
        {
            if (connection.Tag is Session session)
                node.OnSessionClosed(session);
        }
    }

    private void OnClientControl(Session session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint callId = FrameHeader.CallIdOf(in header, frame);
        switch (header.MessageId)
        {
            case ControlOp.PickGateway when callId != 0:
                PickGateway(session, callId);
                break;
            case ControlOp.Ticket:
                IssueTicket(session);
                break;
            case ControlOp.ResumeSession when callId != 0:
                ResumeSession(session, callId, FrameHeader.PayloadOf(in header, frame));
                break;
            case ControlOp.Goodbye:
                session.Explicit = true;
                if (callId != 0) session.Sink.SendOk(callId);
                break;
            default:
                session.Connection.Close(new InvalidOperationException("Unexpected control operation from client: " + header.MessageId));
                break;
        }
    }

    /// <summary>
    /// Validate and route a client message (<see cref="Flow.FromClient"/> messages only) to its handler: the actor the
    /// client named (<see cref="MessageAttribute.AnyId"/> messages only), else the actor of the type handling it that
    /// is bound to the session, else the service handling it. The session is the message's origin (<c>Caller</c> in the
    /// handler).
    /// </summary>
    private void OnClientMessage(Session session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint replyId = FrameHeader.CallIdOf(in header, frame);
        if (!MessageRegistry.TryGetClientMessage(header.MessageId, out var message))
        {
            if (replyId != 0) session.Sink.SendError(replyId, RpcStatus.Forbidden);
            return;
        }

        uint named = header.Target;
        var body = FrameHeader.PayloadOf(in header, frame);
        uint handlerTypeId;
        long key;
        if (named != 0)
        {
            // The client named the actor: only for AnyId messages, and only an actor type handling the message.
            if (!message.AnyId || !HandlesMessage(named, message.Id))
            {
                if (replyId != 0) session.Sink.SendError(replyId, RpcStatus.Forbidden);
                return;
            }

            (handlerTypeId, key) = (named, header.Key);
        }
        else if (View.TryGetRoute(message.Id, actor: true, out var route, out _))
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

        // The payload goes on as is; the origin is this gateway's session, whatever the client wrote.
        IIngress ingress = this;
        var origin = new Origin(Id, session.Id);
        if (replyId == 0) ingress.Send(message.Id, handlerTypeId, key, body, origin);
        else ingress.Ask(message.Id, handlerTypeId, key, body, origin, session.Sink, replyId, header.Budget); // the client's own timeout
    }

    /// <summary>Whether actor type <paramref name="handlerTypeId"/> handles message <paramref name="messageId"/> in the cluster.</summary>
    private bool HandlesMessage(uint handlerTypeId, uint messageId)
    {
        var routes = View.Routes(messageId);
        for (int i = 0; i < routes.Length; i++)
            if (routes[i].HandlerTypeId == handlerTypeId && routes[i].Actor) return true;
        return false;
    }
}

/// <summary>Actor key a session is bound to for one actor handler type.</summary>
internal readonly record struct SessionBinding(uint HandlerTypeId, long Key);

/// <summary>A connected client on this gateway.</summary>
internal sealed class Session(long id, Connection connection)
{
    public long Id { get; } = id;
    public Connection Connection { get; } = connection;
    public IReplySink Sink { get; } = new ConnectionReplySink(connection);

    private volatile SessionBinding[] _bindings = [];

    /// <summary>
    /// Actor keys bound by actors, one per handler type (<c>BindSession</c>): an immutable snapshot, read lock-free.
    /// </summary>
    public SessionBinding[] Bindings => _bindings;

    /// <summary>Groups the session belongs to and its slot in each (changed under the gateway's group gate).</summary>
    public Dictionary<long, int> Groups { get; } = new();

    /// <summary>Set under the gateway's group gate when the session closed: it joins no group afterwards.</summary>
    public bool Closed { get; set; }

    /// <summary>Closed on purpose (client goodbye, <c>Disconnect</c>): its actors learn it at once, no reconnect grace.</summary>
    public volatile bool Explicit;

    /// <summary>Key bound for <paramref name="handlerTypeId"/>, 0 when unbound. Lock-free (client message path).</summary>
    public long KeyOf(uint handlerTypeId)
    {
        var bindings = _bindings;
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].HandlerTypeId == handlerTypeId) return bindings[i].Key;
        return 0;
    }

    /// <summary>
    /// Bind <paramref name="handlerTypeId"/> to <paramref name="key"/> (0 unbinds) and return the previous key.
    /// Callers hold the gateway's group gate; the copy is published to lock-free readers by the volatile write.
    /// </summary>
    public long Bind(uint handlerTypeId, long key)
    {
        var items = _bindings;
        int i = 0;
        while (i < items.Length && items[i].HandlerTypeId != handlerTypeId) i++;
        long previous = i < items.Length ? items[i].Key : 0;
        if (i == items.Length)
        {
            if (key != 0) _bindings = [.. items, new SessionBinding(handlerTypeId, key)];
        }
        else if (key == 0)
        {
            _bindings = [.. items.AsSpan(0, i), .. items.AsSpan(i + 1)];
        }
        else
        {
            var next = (SessionBinding[])items.Clone();
            next[i] = new SessionBinding(handlerTypeId, key);
            _bindings = next;
        }

        return previous;
    }
}

/// <summary>
/// The sessions of one client group on this gateway. Membership changes (under the gateway's group gate) and fan-out
/// both take the group's lock, so a fan-out reaches every session that joined before it (a Bind then a push on one
/// link: the push reaches the session). Join and leave are O(1) (slot per session, swap-remove); a broadcast copies
/// nothing and allocates nothing. Lock order: group gate, group, connection send lock; <see cref="Connection.Send"/>
/// never closes inline, so no close callback re-enters a group during a fan-out.
/// </summary>
internal sealed class GroupMembers(long id)
{
    private readonly Lock _gate = new();
    private Session[] _members = new Session[4];
    private int _count;

    public void Add(Session session)
    {
        lock (_gate)
        {
            if (_count == _members.Length) Array.Resize(ref _members, _count * 2);
            _members[_count] = session;
            session.Groups[id] = _count++;
        }
    }

    /// <summary>Remove <paramref name="session"/> (a member); returns the members left.</summary>
    public int Remove(Session session)
    {
        lock (_gate)
        {
            session.Groups.Remove(id, out int slot);
            var last = _members[--_count];
            _members[_count] = null!;
            if (slot != _count)
            {
                _members[slot] = last;
                last.Groups[id] = slot;
            }

            return _count;
        }
    }

    public void Send(ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            var members = _members;
            for (int i = 0; i < _count; i++) members[i].Connection.Send(frame);
        }
    }

    /// <summary>The members now (a copy: for operations that change memberships, a rare path).</summary>
    public Session[] Snapshot()
    {
        lock (_gate) return _members.AsSpan(0, _count).ToArray();
    }
}
