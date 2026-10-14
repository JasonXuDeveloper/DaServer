using System;
using System.Runtime.CompilerServices;
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

// Call routing: proxies -> local shard mailbox or remote link; inbound frames -> shards; responses -> callers.
public sealed partial class Node : IRpcInvoker, IConnectionHandler
{
    private const int MaxHops = 3;

    private readonly LocalReplySink _localSink;
    private int _roundRobin;

    internal IConnectionHandler ClusterHandler => this;

    // ------------------------------------------------------------------ outgoing calls (IRpcInvoker)

    public ValueTask<T> Call<T>(FrameWriter request, uint methodId, RpcTargetKind kind, long key)
    {
        var call = PendingCall<T>.Rent(Shard.Current);
        var task = call.Task;
        Route(request, methodId, kind, key, call, null, 0);
        return task;
    }

    public ValueTask CallVoid(FrameWriter request, uint methodId, RpcTargetKind kind, long key)
    {
        var call = PendingCall.Rent(Shard.Current);
        var task = call.Task;
        Route(request, methodId, kind, key, call, null, 0);
        return task;
    }

    public void Send(FrameWriter request, uint methodId, RpcTargetKind kind, long key, ulong gatewayNodeId = 0)
    {
        if (kind is RpcTargetKind.Session or RpcTargetKind.Group)
            SendToClients(request, methodId, kind, key, gatewayNodeId);
        else
            Route(request, methodId, kind, key, null, null, 0);
    }

    /// <summary>
    /// Route a request whose payload is already written. Exactly one of <paramref name="call"/> (local caller) or
    /// <paramref name="relaySink"/> (forwarding a call made elsewhere, answered to that sink under
    /// <paramref name="relayRequestId"/>) may be set; neither = one-way. Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal void Route(FrameWriter frame, uint methodId, RpcTargetKind kind, long key, IPendingCall? call,
        IReplySink? relaySink, uint relayRequestId, byte hops = 0, byte flags = 0)
    {
        try
        {
            if (!RpcRegistry.TryGetMethod(methodId, out var method))
                throw new RpcException(RpcStatus.UnknownMethod, "Unknown method " + methodId.ToString("x8"));

            var contract = method.Owner;
            var target = kind == RpcTargetKind.Actor ? View.ActorOwner(contract.Id, key) : PickServiceNode(contract.Id);
            if (target == null)
                throw new RpcException(RpcStatus.NotHosted, "No node hosts " + contract);

            bool local = target.NodeId == Id;
            PeerLink? link = local ? null : GetLink(target);

            if (relaySink != null && relayRequestId != 0)
                call = RelayCall.Rent(relaySink, relayRequestId);

            uint requestId = 0;
            var budget = RpcStatus.Ok;
            if (call != null)
            {
                // The request is worth running until its first caller gives up (a relayed call keeps the deadline it
                // arrived with); the remaining time travels with it.
                long now = Environment.TickCount64;
                if (frame.Deadline == 0) frame.Deadline = now + (long)Options.CallTimeout.TotalMilliseconds;
                requestId = Pending.Register(call, (int)Math.Max(1, frame.Deadline - now), link);
                budget = FrameHeader.EncodeBudget(frame.Deadline - now);
            }

            frame.SetHeader(call != null ? FrameKind.Request : FrameKind.OneWay, requestId, methodId, key, budget, flags, hops);

            if (local)
            {
                DeliverLocal(contract, key, frame, call != null ? _localSink : null);
            }
            else
            {
                link!.Send(frame.Frame);
                frame.Dispose();
            }
        }
        catch (RpcException e)
        {
            frame.Dispose();
            if (call != null) call.Fail(e);
            else if (relaySink != null && relayRequestId != 0) ReplyError(relaySink, relayRequestId, e.Status, e.Message);
        }
    }

    /// <summary>
    /// Forward a request a shard received for an actor this node does not own (stale view at the sender).
    /// Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal void Forward(FrameWriter frame, IReplySink? sink)
    {
        var header = FrameHeader.Read(frame.Frame);
        uint replyId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (header.Hops >= MaxHops)
        {
            frame.Dispose();
            if (sink != null && replyId != 0) ReplyError(sink, replyId, RpcStatus.NotHosted, "Too many forwarding hops");
            return;
        }

        Route(frame, header.MethodId, RpcTargetKind.Actor, header.Key, null, sink, replyId, (byte)(header.Hops + 1), header.Flags);
    }

    private MemberInfo? PickServiceNode(uint contractId)
    {
        if (_implementationByContract.ContainsKey(contractId) && Membership.SelfIsUp)
            return Membership.Self; // prefer local: no network hop

        var nodes = View.ServiceNodes(contractId);
        if (nodes.Length == 0) return null;
        return nodes[(int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)nodes.Length)];
    }

    private void DeliverLocal(RpcContractInfo contract, long key, FrameWriter frame, IReplySink? sink) =>
        Admit(SelectShard(contract, key), frame, sink);

    /// <summary>Hand a call or event to a shard, or refuse it when the shard is full. Takes ownership of <paramref name="frame"/>.</summary>
    internal static void Admit(Shard shard, FrameWriter frame, IReplySink? sink)
    {
        if (!shard.TryAdmit(frame, sink)) ShardDispatch.Overloaded(frame, sink);
    }

    private Shard SelectShard(RpcContractInfo contract, long key)
    {
        bool actor = contract.Kind == RpcContractKind.Actor;
        uint implementationId = _implementationByContract.TryGetValue(contract.Id, out var impl) ? impl.Id : contract.Id;
        return SelectShard(implementationId, actor, key);
    }

    /// <summary>
    /// Shard of an actor (stable hash of implementation and key: every message of an activation reaches the shard
    /// that holds it) or of a service message (the calling shard when local, else round robin).
    /// </summary>
    private Shard SelectShard(uint implementationId, bool actor, long key)
    {
        var shards = _shards;
        if (actor)
            return shards[(int)(ClusterView.Weight((ulong)shards.Length, implementationId, key) % (ulong)shards.Length)];

        var current = Shard.Current;
        if (current != null && current.Node == this) return current;
        return shards[(int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)shards.Length)];
    }

    // ------------------------------------------------------------------ inbound frames (cluster links)

    void IConnectionHandler.OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        if (_killed)
        {
            connection.Close();
            return;
        }

        if (connection.Tag is PeerLink link) link.OnReceived(connection, header.Length);
        switch (header.Kind)
        {
            case FrameKind.Request:
            case FrameKind.OneWay:
                OnInboundCall(connection, in header, frame);
                break;
            case FrameKind.Event:
                OnInboundEvent(in header, frame);
                break;
            case FrameKind.Response:
                CompleteCall(header.RequestId, header.Status, frame);
                break;
            case FrameKind.Push:
            case FrameKind.Broadcast:
                OnClientDelivery(in header, frame);
                break;
            case FrameKind.Control:
                OnControl(connection, in header, frame.Slice(FrameHeader.Size));
                break;
            case FrameKind.Ping:
                var pong = new FrameHeader { Length = FrameHeader.Size, Kind = FrameKind.Pong };
                Span<byte> buffer = stackalloc byte[FrameHeader.Size];
                pong.Write(buffer);
                connection.Send(buffer);
                break;
        }
    }

    void IConnectionHandler.OnClosed(Connection connection, Exception? error)
    {
        if (connection.Tag is PeerLink link)
            link.OnConnectionClosed(connection, error);
    }

    private void OnInboundCall(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var sink = ConnectionReplySink.For(connection);
        uint replyId = header.Kind == FrameKind.Request ? header.RequestId : 0;
        if (!RpcRegistry.TryGetMethod(header.MethodId, out var method))
        {
            if (replyId != 0) ReplyError(sink, replyId, RpcStatus.UnknownMethod, "Unknown method");
            return;
        }

        var contract = method.Owner;
        if (contract.Kind == RpcContractKind.Service && !_implementationByContract.ContainsKey(contract.Id))
        {
            if (replyId != 0) ReplyError(sink, replyId, RpcStatus.NotHosted, contract.ToString());
            return;
        }

        // Actors not owned/hosted here are forwarded by the shard (it re-checks ownership on its own thread).
        var copy = FrameWriter.CopyOf(frame);
        copy.Deadline = header.DeadlineOnArrival();
        Admit(SelectShard(contract, header.Key), copy, sink);
    }

    /// <summary>Complete an outgoing call: on the caller's shard when it has one, inline otherwise.</summary>
    private void CompleteCall(uint requestId, RpcStatus status, ReadOnlySpan<byte> frame)
    {
        var call = Pending.Take(requestId);
        if (call == null) return; // timed out or failed already

        if (call is PendingCallBase { CallerShard: Shard shard })
            shard.Post(new WorkItem(WorkKind.Complete, call, FrameWriter.CopyOf(frame)));
        else
            call.Complete(status, frame.Slice(FrameHeader.Size));
    }

    internal static void ReplyError(IReplySink sink, uint requestId, RpcStatus status, string message)
    {
        var w = FrameWriter.Rent();
        var nino = w.Nino;
        NinoSerializer.Serialize(message, ref nino);
        sink.SendReply(requestId, status, w);
    }

    /// <summary>
    /// The first frame of each node link connection: who we are, so the peer answers through its own link to us, and
    /// which stream continues on it.
    /// </summary>
    internal byte[] HelloFrame(long streamId)
    {
        var self = Membership.Self;
        var w = FrameWriter.Rent();
        var nino = w.Nino;
        NinoSerializer.Serialize(self.Host, ref nino);
        NinoSerializer.Serialize(self.Port, ref nino);
        NinoSerializer.Serialize(streamId, ref nino);
        w.SetHeader(FrameKind.Control, 0, ControlOp.Hello, (long)Id);
        var frame = w.Frame.ToArray(); // once per connection
        w.Dispose();
        return frame;
    }

    /// <summary>A node named itself on an accepted connection: our replies to it go through our link to it.</summary>
    private void OnHello(Connection connection, ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out string host, ref reader);
        NinoDeserializer.Deserialize(out int port, ref reader);
        NinoDeserializer.Deserialize(out long streamId, ref reader);
        GetLink(host, port).OnInboundHello(connection, streamId);
    }

    /// <summary>Our Hello was answered on <paramref name="connection"/>: the link resumes its stream.</summary>
    private static void OnResume(Connection connection, long received, ReadOnlySpan<byte> payload)
    {
        if (connection.Tag is not PeerLink link) return;
        var reader = new Reader(payload);
        NinoDeserializer.Deserialize(out ulong peerId, ref reader);
        link.OnResume(connection, received, peerId);
    }

    /// <summary>The link to <paramref name="member"/> (cached on the member: no lookup on the send path).</summary>
    internal PeerLink GetLink(MemberInfo member) => member.Link ??= GetLink(member.Host, member.Port);

    internal PeerLink GetLink(string host, int port) =>
        _links.TryGetValue((host, port), out var link) ? link : AddLink(host, port);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private PeerLink AddLink(string host, int port)
    {
        lock (_links)
        {
            if (_links.TryGetValue((host, port), out var link)) return link;
            link = new PeerLink(this, host, port);
            _links[(host, port)] = link;
            _linkList = [.. _linkList, link]; // copy on write: the maintenance tick walks it without allocating
            return link;
        }
    }

    // ------------------------------------------------------------------ reply sinks

    /// <summary>Replies to calls made from this node: complete the pending call (on the caller's shard).</summary>
    private sealed class LocalReplySink(Node node) : IReplySink
    {
        public void SendReply(uint requestId, RpcStatus status, FrameWriter response)
        {
            var call = node.Pending.Take(requestId);
            if (call == null)
            {
                response.Dispose();
                return;
            }

            response.SetHeader(FrameKind.Response, requestId, 0, 0, status);
            if (call is PendingCallBase { CallerShard: Shard shard })
            {
                shard.Post(new WorkItem(WorkKind.Complete, call, response)); // zero copy: the frame is handed over
            }
            else
            {
                call.Complete(status, response.Payload);
                response.Dispose();
            }
        }
    }

    /// <summary>Forwards the response of a relayed call to the original caller's sink under its request id.</summary>
    private sealed class RelayCall : IPendingCall
    {
        private static readonly ObjectPool<RelayCall> Pool = new(() => new RelayCall(), 1024);

        private IReplySink _sink = null!;
        private uint _requestId;

        public static RelayCall Rent(IReplySink sink, uint requestId)
        {
            var call = Pool.Rent();
            call._sink = sink;
            call._requestId = requestId;
            return call;
        }

        public void Complete(RpcStatus status, ReadOnlySpan<byte> payload)
        {
            var w = FrameWriter.Rent(FrameHeader.Size + payload.Length);
            w.WriteRaw(payload);
            var (sink, id) = (_sink, _requestId);
            Release();
            sink.SendReply(id, status, w);
        }

        public void Fail(RpcException error)
        {
            var (sink, id) = (_sink, _requestId);
            Release();
            ReplyError(sink, id, error.Status, error.Message);
        }

        private void Release()
        {
            _sink = null!;
            _requestId = 0;
            Pool.Return(this);
        }
    }
}

/// <summary>Replies to a request that arrived on a connection (one instance per connection, created once).</summary>
internal sealed class ConnectionReplySink(Connection connection) : IReplySink
{
    /// <summary>
    /// Where replies to a request received on <paramref name="connection"/> go: the link back to the node that named
    /// itself with a Hello (one ordered stream per direction), else this connection (created once per connection).
    /// </summary>
    public static IReplySink For(Connection connection)
    {
        if (connection.Tag is PeerLink link) return link.ReplySink;
        if (connection.Tag is ConnectionReplySink sink) return sink;
        sink = new ConnectionReplySink(connection);
        connection.Tag = sink;
        return sink;
    }

    public Connection Connection { get; } = connection;

    public void SendReply(uint requestId, RpcStatus status, FrameWriter response)
    {
        response.SetHeader(FrameKind.Response, requestId, 0, 0, status);
        Connection.Send(response.Frame);
        response.Dispose();
    }
}
