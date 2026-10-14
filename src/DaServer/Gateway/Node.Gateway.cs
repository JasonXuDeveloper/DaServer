using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using DaServer.Buffers;
using DaServer.Cluster;
using DaServer.Concurrency;
using DaServer.Net;
using DaServer.Protocol;
using DaServer.Rpc;
using Microsoft.Extensions.Logging;
using Nino.Core;

namespace DaServer.Runtime;

// Gateway capability (NodeOptions.ClientPort): client sessions, routing of client calls into the cluster, push and group broadcast fan-out.
// Client calls are routed by patching header fields; payloads are never deserialized here.
public sealed partial class Node
{
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly ConcurrentDictionary<long, CopyOnWriteArray<Session>> _groups = new();
    private readonly Lock _groupGate = new(); // group membership changes (control path); fan-out reads are lock-free
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
        _clientListener = new Listener(new IPEndPoint(IPAddress.Parse(Options.ClusterBind), port), socket =>
        {
            var connection = new Connection(socket, _gatewayHandler, ConnectionOptions.ForClients());
            var session = new Session(Interlocked.Increment(ref _nextSessionId), connection);
            connection.Tag = session;
            _sessions[session.Id] = session;
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

    public void Bind<TActor>(SessionRef session, long key) where TActor : class =>
        SendGatewayControl(session, ControlOp.Bind, ContractOf<TActor>.Id, key);

    public void Unbind<TActor>(SessionRef session) where TActor : class =>
        SendGatewayControl(session, ControlOp.Unbind, ContractOf<TActor>.Id, 0);

    public void JoinGroup(SessionRef session, ClientGroup group) => SendGatewayControl(session, ControlOp.JoinGroup, 0, group.Id);

    public void LeaveGroup(SessionRef session, ClientGroup group) => SendGatewayControl(session, ControlOp.LeaveGroup, 0, group.Id);

    public void Kick(SessionRef session) => SendGatewayControl(session, ControlOp.Kick, 0, 0);

    private void SendGatewayControl(SessionRef session, uint op, uint contractId, long value)
    {
        var w = FrameWriter.Rent(64);
        var nino = w.Nino;
        NinoSerializer.Serialize(value, ref nino);
        w.SetHeader(FrameKind.Control, contractId, op, session.SessionId);
        if (session.GatewayNodeId == Id)
        {
            OnGatewayControl(op, session.SessionId, contractId, w.Payload);
        }
        else if (View.TryGetMember(session.GatewayNodeId, out var gateway))
        {
            GetLink(gateway).Send(w.Frame);
        }

        w.Dispose();
    }

    /// <summary>Push / broadcast from generated client-callback proxies. Takes ownership of <paramref name="frame"/>.</summary>
    private void SendToClients(FrameWriter frame, uint methodId, RpcTargetKind kind, long key, ulong gatewayNodeId)
    {
        try
        {
            if (kind == RpcTargetKind.Session)
            {
                frame.SetHeader(FrameKind.Push, 0, methodId, key);
                if (gatewayNodeId == Id) DeliverToSession(key, frame.Frame);
                else if (View.TryGetMember(gatewayNodeId, out var gateway)) GetLink(gateway).Send(frame.Frame);
                return;
            }

            // Group broadcast: serialized once, one frame per gateway; each gateway fans out locally.
            frame.SetHeader(FrameKind.Broadcast, 0, methodId, key);
            foreach (var member in View.UpMembers)
            {
                if (member.ClientPort == 0) continue;
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
        if (groupId == 0)
        {
            foreach (var session in _sessions.Values) session.Connection.Send(frame);
            return;
        }

        if (!_groups.TryGetValue(groupId, out var group)) return;
        var members = group.Items; // immutable snapshot
        for (int i = 0; i < members.Length; i++)
            members[i].Connection.Send(frame);
    }

    private void OnControl(Connection connection, in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.MethodId)
        {
            case ControlOp.Gossip:
            case ControlOp.GossipReply:
                Membership.OnGossip(connection, header.MethodId, payload);
                break;
            default:
                OnGatewayControl(header.MethodId, header.Key, header.RequestId, payload);
                break;
        }
    }

    private void OnGatewayControl(uint op, long sessionId, uint contractId, ReadOnlySpan<byte> payload)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out long value, ref reader);
        switch (op)
        {
            case ControlOp.Bind:
                session.Bindings.Set(contractId, new SessionBinding(contractId, value), static (b, c) => b.ContractId == c);
                break;
            case ControlOp.Unbind:
                session.Bindings.Remove(contractId, static (b, c) => b.ContractId == c);
                break;
            case ControlOp.JoinGroup:
                lock (_groupGate)
                {
                    if (session.Closed) return;
                    _groups.GetOrAdd(value, static _ => new CopyOnWriteArray<Session>()).Add(session);
                    session.Groups.Add(value);
                }

                break;
            case ControlOp.LeaveGroup:
                lock (_groupGate) Leave(session, value);
                break;
            case ControlOp.Kick:
                session.Connection.Close();
                break;
        }
    }

    /// <summary>Remove a session from a group; empty groups are dropped (bounded memory). Caller holds the gate.</summary>
    private void Leave(Session session, long groupId)
    {
        session.Groups.Remove(groupId);
        if (!_groups.TryGetValue(groupId, out var group)) return;
        group.Remove(session);
        if (group.Items.Length == 0) _groups.TryRemove(groupId, out _);
    }

    /// <summary>Drop the session from its groups and tell every actor it was bound to (<see cref="SessionClosed"/>).</summary>
    private void OnSessionClosed(Session session)
    {
        _sessions.TryRemove(session.Id, out _);
        lock (_groupGate)
        {
            session.Closed = true;
            foreach (var groupId in session.Groups.Items) Leave(session, groupId);
        }

        var sessionRef = new SessionRef(Id, session.Id);
        foreach (var binding in session.Bindings.Items)
            PublishTo(binding.ContractId, new SessionClosed(sessionRef, binding.Key));
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
                    node.OnClientCall(session, in header, frame);
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
    /// Validate and route a client call by the method's <see cref="ClientAttribute"/>: services, actors on the key bound
    /// to the session, or on the key the client chose (AnyKey). The caller session is prepended for the handler.
    /// </summary>
    private void OnClientCall(Session session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint replyId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (!RpcRegistry.TryGetMethod(header.MethodId, out var method) || method.ClientAccess == ClientAccess.None)
        {
            if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.Forbidden, "Not client accessible");
            return;
        }

        var contract = method.Owner;
        var (kind, key) = method.ClientAccess switch
        {
            ClientAccess.SessionKey => (RpcTargetKind.Actor, session.KeyOf(contract.Id)),
            ClientAccess.AnyKey => (RpcTargetKind.Actor, header.Key),
            _ => (RpcTargetKind.Service, 0L),
        };
        if (kind == RpcTargetKind.Actor && key == 0)
        {
            if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.Forbidden, "Session not bound to " + contract);
            return;
        }

        var w = FrameWriter.Rent(frame.Length + 32);
        var nino = w.Nino;
        NinoSerializer.Serialize(new SessionRef(Id, session.Id), ref nino);
        w.WriteRaw(frame.Slice(FrameHeader.Size));
        Route(w, header.MethodId, kind, key, null, replyId != 0 ? session.Sink : null, replyId, flags: FrameFlags.HasCaller);
    }
}

/// <summary>Actor key a session is bound to for one contract.</summary>
internal readonly record struct SessionBinding(uint ContractId, long Key);

/// <summary>A connected client on this gateway.</summary>
internal sealed class Session(long id, Connection connection)
{
    public long Id { get; } = id;
    public Connection Connection { get; } = connection;
    public ConnectionReplySink Sink { get; } = new(connection);

    /// <summary>Actor keys bound by servers, one per contract (<c>Cluster.Bind&lt;TActor&gt;</c>).</summary>
    public CopyOnWriteArray<SessionBinding> Bindings { get; } = new();

    /// <summary>Groups the session belongs to (to leave them when it closes).</summary>
    public CopyOnWriteArray<long> Groups { get; } = new();

    /// <summary>Set under the gateway's group gate when the session closed: it joins no group afterwards.</summary>
    public bool Closed { get; set; }

    /// <summary>Key bound for <paramref name="contractId"/>, 0 when unbound. Lock-free (client call path).</summary>
    public long KeyOf(uint contractId)
    {
        var bindings = Bindings.Items;
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].ContractId == contractId) return bindings[i].Key;
        return 0;
    }
}
