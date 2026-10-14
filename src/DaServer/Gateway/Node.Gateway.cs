using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using DaServer.Buffers;
using DaServer.Cluster;
using DaServer.Net;
using DaServer.Protocol;
using DaServer.Rpc;
using Microsoft.Extensions.Logging;
using Nino.Core;

namespace DaServer.Runtime;

// Gateway role: game-client sessions, routing of client calls into the cluster, push and group broadcast fan-out.
// Client calls are routed by patching header fields; payloads are never deserialized here.
public sealed partial class Node
{
    private readonly ConcurrentDictionary<long, Session> _sessions = new();
    private readonly ConcurrentDictionary<long, SessionGroup> _groups = new();
    private Listener? _clientListener;
    private GatewayHandler? _gatewayHandler;
    private long _nextSessionId;

    /// <summary>Port game clients connect to (0 when this node is not a gateway).</summary>
    public int ClientPort { get; private set; }

    /// <summary>Connected client sessions on this gateway.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Fired on the IO thread when a client session closes (e.g. notify the bound actor).</summary>
    public event Action<SessionRef, long>? SessionClosed;

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

    // ------------------------------------------------------------------ server-side API (any node)

    /// <summary>Bind a client session to an actor key: its calls to [ClientAccessible] actors target that key.</summary>
    public void BindSession(SessionRef session, long actorKey) => SendGatewayControl(session, ControlOp.BindSession, actorKey);

    /// <summary>Add a session to a broadcast group (AOI area, guild channel, chat room...).</summary>
    public void JoinGroup(SessionRef session, ClientGroup group) => SendGatewayControl(session, ControlOp.JoinGroup, group.Id);

    /// <summary>Remove a session from a broadcast group.</summary>
    public void LeaveGroup(SessionRef session, ClientGroup group) => SendGatewayControl(session, ControlOp.LeaveGroup, group.Id);

    /// <summary>Disconnect a client session.</summary>
    public void Kick(SessionRef session) => SendGatewayControl(session, ControlOp.Kick, 0);

    private void SendGatewayControl(SessionRef session, uint op, long value)
    {
        var w = FrameWriter.Rent(64);
        var nino = w.Nino;
        NinoSerializer.Serialize(value, ref nino);
        w.SetHeader(FrameKind.Control, 0, op, session.SessionId);
        if (session.GatewayNodeId == Id)
        {
            OnGatewayControl(op, session.SessionId, w.Payload);
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
        var members = group.Members; // immutable snapshot
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
                OnGatewayControl(header.MethodId, header.Key, payload);
                break;
        }
    }

    private void OnGatewayControl(uint op, long sessionId, ReadOnlySpan<byte> payload)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out long value, ref reader);
        switch (op)
        {
            case ControlOp.BindSession:
                session.BoundKey = value;
                break;
            case ControlOp.JoinGroup:
                _groups.GetOrAdd(value, static _ => new SessionGroup()).Add(session);
                session.AddGroup(value);
                break;
            case ControlOp.LeaveGroup:
                if (_groups.TryGetValue(value, out var group)) group.Remove(session);
                session.RemoveGroup(value);
                break;
            case ControlOp.Kick:
                session.Connection.Close();
                break;
        }
    }

    private void OnSessionClosed(Session session)
    {
        _sessions.TryRemove(session.Id, out _);
        foreach (var groupId in session.Groups)
            if (_groups.TryGetValue(groupId, out var group))
                group.Remove(session);

        SessionClosed?.Invoke(new SessionRef(Id, session.Id), session.BoundKey);
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
    /// Validate and route a client call: only [ClientAccessible] contracts, actor keys come from the session binding
    /// (unless the contract allows any key), and the caller session is prepended so handlers know who called.
    /// </summary>
    private void OnClientCall(Session session, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        uint replyId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (!RpcRegistry.TryGetMethod(header.MethodId, out var method) || !method.Owner.ClientAccessible ||
            method.Owner.Kind == RpcContractKind.ClientCallback)
        {
            if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.Forbidden, "Not client accessible");
            return;
        }

        var contract = method.Owner;
        long key = 0;
        var kind = RpcTargetKind.Service;
        if (contract.Kind == RpcContractKind.Actor)
        {
            kind = RpcTargetKind.Actor;
            key = contract.AnyKey ? header.Key : session.BoundKey;
            if (key == 0)
            {
                if (replyId != 0) ReplyError(session.Sink, replyId, RpcStatus.Forbidden, "Session not bound");
                return;
            }
        }

        var w = FrameWriter.Rent(frame.Length + 32);
        var nino = w.Nino;
        NinoSerializer.Serialize(new SessionRef(Id, session.Id), ref nino);
        w.WriteRaw(frame.Slice(FrameHeader.Size));
        Route(w, header.MethodId, kind, key, null, replyId != 0 ? session.Sink : null, replyId, flags: FrameFlags.HasCaller);
    }
}

/// <summary>A connected game client on this gateway.</summary>
internal sealed class Session
{
    private long[] _groups = [];

    public Session(long id, Connection connection)
    {
        Id = id;
        Connection = connection;
        Sink = new ConnectionReplySink(connection);
    }

    public long Id { get; }
    public Connection Connection { get; }
    public ConnectionReplySink Sink { get; }

    /// <summary>Actor key bound by the server (e.g. player id after login); 0 = unbound.</summary>
    public long BoundKey { get; set; }

    public long[] Groups => Volatile.Read(ref _groups);

    public void AddGroup(long id)
    {
        lock (this)
        {
            if (Array.IndexOf(_groups, id) < 0) Volatile.Write(ref _groups, [.. _groups, id]);
        }
    }

    public void RemoveGroup(long id)
    {
        lock (this)
        {
            int i = Array.IndexOf(_groups, id);
            if (i < 0) return;
            var next = new long[_groups.Length - 1];
            Array.Copy(_groups, 0, next, 0, i);
            Array.Copy(_groups, i + 1, next, i, _groups.Length - i - 1);
            Volatile.Write(ref _groups, next);
        }
    }
}

/// <summary>Sessions of a broadcast group: copy-on-write array so fan-out iterates without locking.</summary>
internal sealed class SessionGroup
{
    private readonly Lock _gate = new();
    private Session[] _members = [];

    public Session[] Members => Volatile.Read(ref _members);

    public void Add(Session session)
    {
        lock (_gate)
        {
            if (Array.IndexOf(_members, session) >= 0) return;
            Volatile.Write(ref _members, [.. _members, session]);
        }
    }

    public void Remove(Session session)
    {
        lock (_gate)
        {
            int i = Array.IndexOf(_members, session);
            if (i < 0) return;
            var next = new Session[_members.Length - 1];
            Array.Copy(_members, 0, next, 0, i);
            Array.Copy(_members, i + 1, next, i, _members.Length - i - 1);
            Volatile.Write(ref _members, next);
        }
    }
}
