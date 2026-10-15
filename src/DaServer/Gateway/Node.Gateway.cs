using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Cluster;
using DaServer.Concurrency;
using DaServer.Net;
using DaServer.Protocol;
using DaServer.Rpc;
using Microsoft.Extensions.Logging;
using Nino.Core;

namespace DaServer.Runtime;

// Gateway capability (NodeOptions.ClientPort): client sessions, routing of client messages into the cluster, push and
// group broadcast fan-out. Client messages are routed by patching header fields; payloads are never deserialized here.
public sealed partial class Node
{
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly ConcurrentDictionary<long, GroupMembers> _groups = new();
    private readonly Lock _groupGate = new(); // membership changes (control path); fan-out takes only its group's lock
    private Listener? _clientListener;
    private GatewayHandler? _gatewayHandler;
    private long _nextSessionId;

    /// <summary>Port clients connect to (0 when this node accepts no clients).</summary>
    public int ClientPort { get; private set; }

    /// <summary>Connected client sessions on this gateway.</summary>
    public int SessionCount => _sessions.Count;

    private void StartGateway()
    {
        if (Options.ClientPort is not { } port) return;
        _gatewayHandler = new GatewayHandler(this);
        var sessionOptions = ConnectionOptions.ForClients();
        sessionOptions.MaxFrameSize = Options.MaxClientFrameSize; // what an untrusted client may make us buffer
        _clientListener = new Listener(new IPEndPoint(IPAddress.Parse(Options.ListenAddress), port), socket =>
        {
            var connection = new Connection(socket, _gatewayHandler, sessionOptions);
            var session = new Session(Interlocked.Increment(ref _nextSessionId), connection);
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

    public void Send<T>(SessionRef session, in T message) where T : struct =>
        SendToClients(MessageWriter.Write(in message), MessageType<T>.Id, group: false, session.SessionId, session.GatewayNodeId);

    public void Send<T>(ClientGroup group, in T message) where T : struct =>
        SendToClients(MessageWriter.Write(in message), MessageType<T>.Id, group: true, group.Id, 0);

    public void JoinGroup(SessionRef session, ClientGroup group) => SendSessionControl(session, ControlOp.JoinGroup, 0, group.Id);

    public void LeaveGroup(SessionRef session, ClientGroup group) => SendSessionControl(session, ControlOp.LeaveGroup, 0, group.Id);

    public void Disconnect(SessionRef session) => SendSessionControl(session, ControlOp.Disconnect, 0, 0);

    /// <summary>
    /// Bind (or unbind) a session to an actor (<see cref="Actor.BindSession"/>): completes once the session's gateway
    /// applied it, so the client's next message already reaches the actor.
    /// </summary>
    internal ValueTask BindSession(SessionRef session, uint implementationId, long key, bool bind)
    {
        uint op = bind ? ControlOp.Bind : ControlOp.Unbind;
        if (session.GatewayNodeId == Id)
        {
            var w = SessionControl(op, 0, session.SessionId, implementationId, key);
            var status = OnSessionControl(op, session.SessionId, w.Payload);
            w.Dispose();
            return status == RpcStatus.Ok ? default : ValueTask.FromException(new RpcException(status, "Session is gone"));
        }

        var link = LinkTo(session.GatewayNodeId);
        if (link == null) return ValueTask.FromException(new RpcException(RpcStatus.Disconnected, "Gateway is gone"));
        var call = PendingCall.Rent(Shard.Current);
        var task = call.Task;
        uint requestId = Pending.Register(call, (int)Options.CallTimeout.TotalMilliseconds, link);
        var frame = SessionControl(op, requestId, session.SessionId, implementationId, key);
        link.Send(frame.Frame);
        frame.Dispose();
        return task;
    }

    private void SendSessionControl(SessionRef session, uint op, uint scope, long value)
    {
        var w = SessionControl(op, 0, session.SessionId, scope, value);
        if (session.GatewayNodeId == Id) OnSessionControl(op, session.SessionId, w.Payload);
        else LinkTo(session.GatewayNodeId)?.Send(w.Frame);
        w.Dispose();
    }

    /// <summary>A session control frame: Key = session id, RequestId = call to acknowledge (0: none), payload = scope + value.</summary>
    private static FrameWriter SessionControl(uint op, uint requestId, long sessionId, uint scope, long value)
    {
        var w = FrameWriter.Rent(64);
        var nino = w.Nino;
        NinoSerializer.Serialize(scope, ref nino);
        NinoSerializer.Serialize(value, ref nino);
        w.SetHeader(FrameKind.Control, requestId, op, sessionId);
        return w;
    }

    /// <summary>Push / broadcast a message to clients. Takes ownership of <paramref name="frame"/>.</summary>
    private void SendToClients(FrameWriter frame, uint messageId, bool group, long key, ulong gatewayNodeId)
    {
        try
        {
            if (!group)
            {
                frame.SetHeader(FrameKind.Push, 0, messageId, key);
                if (gatewayNodeId == Id) DeliverToSession(key, frame.Frame);
                else LinkTo(gatewayNodeId)?.Send(frame.Frame);
                return;
            }

            // Group broadcast: serialized once, one frame per gateway; each gateway fans out locally.
            frame.SetHeader(FrameKind.Broadcast, 0, messageId, key);
            foreach (var member in View.UpMembers)
            {
                if (member.ClientPort == 0 || member.Balancer) continue; // a balancer keeps no sessions
                if (member.NodeId == Id) DeliverToGroup(key, frame.Frame);
                else GetLink(member).Send(frame.Frame);
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>Push/broadcast frame arriving from another node.</summary>
    private void OnClientDelivery(in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        if (header.Kind == FrameKind.Push) DeliverToSession(header.Key, frame);
        else DeliverToGroup(header.Key, frame);
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

    private void OnControl(Connection connection, in FrameHeader header, ReadOnlySpan<byte> payload)
    {
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
            default:
                var status = OnSessionControl(header.MessageId, header.Key, payload);
                if (header.RequestId == 0) break;
                if (status == RpcStatus.Ok) ConnectionReplySink.For(connection).SendReply(header.RequestId, status, FrameWriter.Rent(FrameHeader.Size));
                else ReplyError(ConnectionReplySink.For(connection), header.RequestId, status, "Session is gone");
                break;
        }
    }

    private RpcStatus OnSessionControl(uint op, long sessionId, ReadOnlySpan<byte> payload)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return RpcStatus.Disconnected;
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out uint scope, ref reader);
        NinoDeserializer.Deserialize(out long value, ref reader);
        switch (op)
        {
            case ControlOp.Bind:
            {
                // A bound session also joins the actor's group, so Cluster.Send(actor.Group, ..) reaches it.
                long previous = session.KeyOf(scope);
                session.Bindings.Set(scope, new SessionBinding(scope, value), static (b, id) => b.ImplementationId == id);
                lock (_groupGate)
                {
                    if (previous != 0 && previous != value) Leave(session, ClientGroup.Of(scope, previous).Id);
                    Join(session, ClientGroup.Of(scope, value).Id);
                }

                break;
            }
            case ControlOp.Unbind:
            {
                long previous = session.KeyOf(scope);
                session.Bindings.Remove(scope, static (b, id) => b.ImplementationId == id);
                if (previous != 0) lock (_groupGate) Leave(session, ClientGroup.Of(scope, previous).Id);
                break;
            }
            case ControlOp.JoinGroup:
                lock (_groupGate) Join(session, value);
                break;
            case ControlOp.LeaveGroup:
                lock (_groupGate) Leave(session, value);
                break;
            case ControlOp.Disconnect:
                session.Connection.Close();
                break;
        }

        return RpcStatus.Ok;
    }

    /// <summary>Add a session to a group (once). CallerSession holds the gate.</summary>
    private void Join(Session session, long groupId)
    {
        if (session.Closed || session.Groups.ContainsKey(groupId)) return;
        if (!_groups.TryGetValue(groupId, out var group)) _groups[groupId] = group = new GroupMembers(groupId);
        group.Add(session);
    }

    /// <summary>Remove a session from a group; empty groups are dropped (bounded memory). CallerSession holds the gate.</summary>
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
        foreach (var binding in session.Bindings.Items)
            SendTo(binding.ImplementationId, binding.Key, new SessionClosed(sessionRef));
    }

    /// <summary>
    /// Answer "which gateway should I use": the less loaded of two random gateways that accept sessions (power of two
    /// choices: even spread from gossiped counts, no herd on the single least loaded one while counts are stale).
    /// </summary>
    private void PickGateway(Session session, uint requestId)
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
            ReplyError(session.Sink, requestId, RpcStatus.NotHosted, "No gateway accepts sessions");
            return;
        }

        var pick = second != null && second.ExpectedSessions < first.ExpectedSessions ? second : first;
        Interlocked.Increment(ref pick.Directed);
        var w = FrameWriter.Rent(64);
        var nino = w.Nino;
        NinoSerializer.Serialize(new GatewayEndpoint(pick.ClientHost, pick.ClientPort), ref nino);
        session.Sink.SendReply(requestId, RpcStatus.Ok, w);
    }

    // ------------------------------------------------------------------ client connections

    private sealed class GatewayHandler(Node node) : IConnectionHandler
    {
        public void OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
        {
            var session = (Session)connection.Tag!;
            switch (header.Kind)
            {
                case FrameKind.Request:
                case FrameKind.OneWay:
                    node.OnClientMessage(session, in header, frame);
                    break;
                case FrameKind.Control when header.MessageId == ControlOp.PickGateway && header.RequestId != 0:
                    node.PickGateway(session, header.RequestId);
                    break;
                case FrameKind.Ping:
                    Span<byte> pong = stackalloc byte[FrameHeader.Size];
                    new FrameHeader { Length = FrameHeader.Size, Kind = FrameKind.Pong }.Write(pong);
                    connection.Send(pong);
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

    /// <summary>
    /// Validate and route a client message (<see cref="ClientMessageAttribute"/> types only) to its handler: a service,
    /// or an actor on the key bound to the session (or the key the client chose: AnyKey). The caller session is
    /// prepended for the handler (<c>CallerSession</c>).
    /// </summary>
    private void OnClientMessage(Session session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint replyId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (!MessageRegistry.TryGetClientMessage(header.MessageId, out var message))
        {
            if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.Forbidden, "Not a client message");
            return;
        }

        bool actor = View.TryGetRoute(message.Id, actor: true, out var route, out _);
        if (!actor && !View.TryGetRoute(message.Id, actor: false, out route, out _))
        {
            if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.NotHosted, "No handler for " + message.Type.Name);
            return;
        }

        long key = !actor ? 0 : message.AnyKey ? header.Key : session.KeyOf(route.ImplementationId);
        if (actor && key == 0 && !message.AnyKey)
        {
            if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.Forbidden, "Session not bound to a handler of " + message.Type.Name);
            return;
        }

        var w = FrameWriter.Rent(frame.Length + 32);
        var nino = w.Nino;
        NinoSerializer.Serialize(new SessionRef(Id, session.Id), ref nino);
        w.WriteRaw(frame.Slice(FrameHeader.Size));
        if (replyId == 0)
        {
            SendMessage(w, message.Id, key, actor, FrameFlags.HasCaller);
            return;
        }

        w.Deadline = header.DeadlineOnArrival(); // the client's own timeout, when it sent one
        Request(w, message.Id, key, actor, null, session.Sink, replyId, flags: FrameFlags.HasCaller);
    }
}

/// <summary>Actor key a session is bound to for one actor implementation.</summary>
internal readonly record struct SessionBinding(uint ImplementationId, long Key);

/// <summary>A connected client on this gateway.</summary>
internal sealed class Session(long id, Connection connection)
{
    public long Id { get; } = id;
    public Connection Connection { get; } = connection;
    public ConnectionReplySink Sink { get; } = new(connection);

    /// <summary>Actor keys bound by actors, one per implementation (<c>BindSession</c>).</summary>
    public CopyOnWriteArray<SessionBinding> Bindings { get; } = new();

    /// <summary>Groups the session belongs to and its slot in each (changed under the gateway's group gate).</summary>
    public Dictionary<long, int> Groups { get; } = new();

    /// <summary>Set under the gateway's group gate when the session closed: it joins no group afterwards.</summary>
    public bool Closed { get; set; }

    /// <summary>Key bound for <paramref name="implementationId"/>, 0 when unbound. Lock-free (client message path).</summary>
    public long KeyOf(uint implementationId)
    {
        var bindings = Bindings.Items;
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].ImplementationId == implementationId) return bindings[i].Key;
        return 0;
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
}
