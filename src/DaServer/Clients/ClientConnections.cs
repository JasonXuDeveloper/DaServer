namespace DaServer.Clients;

/// <summary>
/// Client connections (ADR-048): game clients, bots and tools connect to this node (a client-facing node, the
/// deployment's "gateway"), get a session, and their <see cref="FromClientAttribute">[FromClient]</see> messages enter the cluster through
/// the node's ingress with the session as origin. Messages to sessions and client groups are pushed to the sockets
/// (a group message is serialized once and fanned out here); session operations (<see cref="JoinGroup"/>,
/// <see cref="LeaveGroup"/>, <see cref="Disconnect"/>, bindings) are applied here; resume tickets let a client
/// resume on any client-facing node. Payloads are never deserialized on the way in.
/// </summary>
public sealed partial class ClientConnections(ClientConnectionOptions options) : NodeFeature
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
        if (Volatile.Read(ref session.Resuming) == 1)
        {
            session.RetireAtMs = now; // mid-resume, no grants yet: closing it now would cut the resume off; next sweep
            return;
        }

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


    /// <summary>Report the lost connections whose grace ended, drop long-empty groups, close silent and aged connections.</summary>
    protected internal override void Maintain(long nowMs, bool late)
    {
        if (_stopped) return;
        var expired = new GraceExpired(this);
        lock (_graces) _graces.Advance(ref expired);
        if (nowMs >= _nextGroupSweep) SweepGroups(nowMs);
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
}
