namespace DaServer.Runtime;

// Request routing (Ask): local shard or remote link; inbound frames -> shards; replies -> callers.
sealed partial class Node : IChannelHandler
{
    private const int MaxHops = 3;

    private readonly LocalReplySink _localSink;

    internal IChannelHandler ClusterHandler => this;

    /// <summary>
    /// Route a request whose payload is already written to actor (<paramref name="handlerTypeId"/>,
    /// <paramref name="key"/>), or with handler type 0 to the one service handling <paramref name="messageId"/>.
    /// Exactly one of <paramref name="call"/> (local caller) or <paramref name="relaySink"/> (forwarding a request made
    /// elsewhere, answered to that sink under <paramref name="relayCallId"/>) is set. The frame gets this hop's call
    /// id (a forwarded frame's is rewritten). Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal void Request(FrameWriter frame, uint messageId, uint handlerTypeId, long key, IPendingCall? call,
        IReplySink? relaySink, uint relayCallId, byte hops = 0, byte flags = 0, bool refuseLoaded = false)
    {
        ClusterMember? target;
        if (!TryRoute(messageId, handlerTypeId, out var route, out bool ambiguous) ||
            (target = View.Target(in route, messageId, key, Id)) == null)
        {
            if (ambiguous) Refuse(frame, call, relaySink, relayCallId, RpcStatus.Error, AmbiguousMessage(messageId));
            else Refuse(frame, call, relaySink, relayCallId, RpcStatus.NotHosted);
            return;
        }

        bool local = target.NodeId == Id;
        if (refuseLoaded && !local && Options.IngressLoadLimit > 0 && target.Load >= Options.IngressLoadLimit)
        {
            Refuse(frame, call, relaySink, relayCallId, RpcStatus.Overloaded); // it would refuse it anyway, later
            return;
        }

        PeerLink? link = local ? null : GetLink(target);
        call ??= RelayCall.Rent(relaySink!, relayCallId);

        // The request is worth running until its first caller gives up (a relayed request keeps the deadline it
        // arrived with); the remaining time travels with it.
        long now = Platform.Milliseconds; // one clock read for the deadline and the budget
        if (frame.Deadline == 0) frame.Deadline = now + (long)Options.CallTimeout.TotalMilliseconds;
        if (!Pending.TryRegister(call, frame.Deadline, out uint callId, link))
        {
            Refuse(frame, call, null, 0, RpcStatus.Overloaded);
            return;
        }

        var budget = FrameHeader.EncodeBudget(frame.Deadline - now);
        frame.SetCallId(callId);
        frame.SetHeader(FrameKind.Message, route.HandlerTypeId, messageId, key, budget, flags, hops);

        if (local)
        {
            Admit(SelectShard(route.HandlerTypeId, route.Actor, key), frame, _localSink);
        }
        else
        {
            link!.Send(frame.Frame);
            frame.Dispose();
        }
    }

    /// <summary>
    /// Ask something node <paramref name="node"/> holds (a session operation): the frame (payload written) goes on the
    /// ordered link to that node, so it arrives behind everything sent there before it. Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    internal void AskNode(FrameWriter frame, uint scope, uint messageId, ulong node, long value, IPendingCall call)
    {
        PeerLink? link = node == Id ? null : LinkTo(node);
        if (link == null && node != Id)
        {
            Refuse(frame, call, null, 0, RpcStatus.Disconnected);
            return;
        }

        long timeout = (long)Options.CallTimeout.TotalMilliseconds;
        if (!Pending.TryRegister(call, PendingCallTable.DeadlineAfter(Options.CallTimeout), out uint callId, link))
        {
            Refuse(frame, call, null, 0, RpcStatus.Overloaded);
            return;
        }

        frame.SetCallId(callId);
        frame.SetHeader(FrameKind.Message, scope, messageId, value, FrameHeader.EncodeBudget(timeout));
        if (link == null) DeliverToAddress(frame.Frame, _localSink);
        else link.Send(frame.Frame);
        frame.Dispose();
    }

    /// <summary>Fail a request that cannot be sent: status-only, nothing formatted. Takes ownership of <paramref name="frame"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Refuse(FrameWriter frame, IPendingCall? call, IReplySink? relaySink, uint relayCallId,
        RpcStatus status, string? message = null)
    {
        frame.Dispose();
        if (call != null) call.Fail(status, message);
        else relaySink!.SendError(relayCallId, status, message);
    }

    /// <summary>
    /// Forward a message a shard received for an actor this node does not own (stale view at the sender).
    /// Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal void Forward(FrameWriter frame, IReplySink? sink)
    {
        var header = FrameHeader.Read(frame.Frame);
        if (!header.IsRequest)
        {
            ForwardMessage(frame);
            return;
        }

        uint callId = frame.CallId;
        if (header.Hops >= MaxHops)
        {
            frame.Dispose();
            sink?.SendError(callId, RpcStatus.NotHosted);
            return;
        }

        Request(frame, header.MessageId, header.Target, header.Key, null, sink!, callId, (byte)(header.Hops + 1), header.Flags);
    }

    /// <summary>Hand a call or event to a shard, or refuse it when the shard is full. Takes ownership of <paramref name="frame"/>.</summary>
    internal static void Admit(Shard shard, FrameWriter frame, IReplySink? sink)
    {
        if (!shard.TryAdmit(frame, sink)) ShardDispatch.Overloaded(frame, sink);
    }

    /// <summary>
    /// Shard of an actor (stable hash of handler type and key: every message of a loaded actor reaches the shard
    /// that holds it) or of a service message (the calling shard when local, else round robin).
    /// </summary>
    private Shard SelectShard(uint handlerTypeId, bool actor, long key)
    {
        var shards = _shards;
        if (actor)
        {
            uint group = Placements.Of(handlerTypeId, ref key); // actors placed together share a shard too
            return shards[(int)(ClusterView.Weight((ulong)shards.Length, group, key) % (ulong)shards.Length)];
        }

        var current = Shard.Current;
        if (current != null && current.Node == this) return current;
        return shards[(int)(Spread.Next() % (uint)shards.Length)];
    }

    // ------------------------------------------------------------------ inbound frames (cluster links)

    void IChannelHandler.OnFrame(IFrameChannel channel, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var connection = (Connection)channel; // node links are TCP connections
        if (_killed)
        {
            connection.Close();
            return;
        }

        if (connection.Tag is PeerLink link) link.OnReceived(connection, header.Length);
        else if (header.Kind != FrameKind.Control || header.MessageId != ControlOp.Hello)
        {
            // An accepted connection says who it is (an authenticated Hello) before anything else is read.
            connection.Close(NotIntroduced);
            return;
        }
        switch (header.Kind)
        {
            case FrameKind.Message:
                OnInboundMessage(connection, in header, frame);
                break;
            case FrameKind.Reply:
                CompleteCall(FrameHeader.CallIdOf(in header, frame), header.Status, frame);
                break;
            case FrameKind.Control:
                OnControl(connection, in header, frame);
                break;
        }
    }

    void IChannelHandler.OnClosed(IFrameChannel channel, Exception? error)
    {
        if (channel.Tag is PeerLink link)
            link.OnConnectionClosed((Connection)channel, error);
    }

    private void OnControl(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var payload = FrameHeader.PayloadOf(in header, frame);
        switch (header.MessageId)
        {
            case ControlOp.Hello:
                OnHello(connection, (ulong)header.Key, payload);
                break;
            case ControlOp.Challenge:
                (connection.Tag as PeerLink)?.OnChallenge(connection, payload);
                break;
            case ControlOp.Resume:
                OnResume(connection, header.Key, payload);
                break;
            case ControlOp.Ping:
                KeepAlive.Pong(connection);
                break;
            case ControlOp.Pong:
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
            case ControlOp.SlotTable:
                _slots.OnTable(NinoDeserializer.Deserialize<SlotTable>(payload));
                break;
            case ControlOp.SlotReport:
                _slots.OnReport(NinoDeserializer.Deserialize<SlotReport>(payload));
                break;

        }
    }

    /// <summary>Complete an outgoing call: on the caller's shard when it has one, inline otherwise.</summary>
    private void CompleteCall(uint callId, RpcStatus status, ReadOnlySpan<byte> frame)
    {
        var call = Pending.Take(callId);
        if (call == null) return; // timed out or failed already

        if (call.Caller is Shard shard)
            shard.Post(new WorkItem(WorkKind.Complete, call, FrameWriter.CopyOf(frame)));
        else
            call.Complete(status, FrameHeader.PayloadOf(FrameHeader.Read(frame), frame));
    }

    // ------------------------------------------------------------------ reply sinks

    /// <summary>Replies to calls made from this node: complete the pending call (on the caller's shard).</summary>
    private sealed class LocalReplySink(Node node) : IReplySink
    {
        public void SendReply(uint callId, RpcStatus status, FrameWriter reply)
        {
            var call = node.Pending.Take(callId);
            if (call == null)
            {
                reply.Dispose();
                return;
            }

            reply.SetHeader(FrameKind.Reply, 0, 0, 0, status); // local: no call id trailer needed
            if (call.Caller is Shard shard)
            {
                shard.Post(new WorkItem(WorkKind.Complete, call, reply)); // zero copy: the frame is handed over
            }
            else
            {
                call.Complete(status, reply.Payload);
                reply.Dispose();
            }
        }
    }

    /// <summary>Forwards the reply of a relayed call to the original caller's sink under its call id.</summary>
    private sealed class RelayCall : IPendingCall
    {
        private static readonly ObjectPool<RelayCall> Pool = new(() => new RelayCall(), 1 << 16); // calls relayed at once by a client-facing node

        public CompletionThread? Caller => null; // forwarded inline from the IO thread

        private IReplySink _sink = null!;
        private uint _callId;

        public static RelayCall Rent(IReplySink sink, uint callId)
        {
            var call = Pool.Rent();
            call._sink = sink;
            call._callId = callId;
            return call;
        }

        public void Complete(RpcStatus status, ReadOnlySpan<byte> payload)
        {
            var w = FrameWriter.Rent(FrameHeader.Size + payload.Length + sizeof(uint)); // the call id is a trailer
            w.WriteRaw(payload);
            var (sink, id) = (_sink, _callId);
            Release();
            sink.SendReply(id, status, w);
        }

        public void Fail(RpcStatus status, string? message = null, TimeSpan retryAfter = default)
        {
            var (sink, id) = (_sink, _callId);
            Release();
            if (retryAfter > TimeSpan.Zero) sink.SendRefusal(id, status, retryAfter);
            else sink.SendError(id, status, message);
        }

        private void Release()
        {
            _sink = null!;
            _callId = 0;
            Pool.Return(this);
        }
    }
}

/// <summary>Replies and control frames back on a channel: a client's, or a connection a request arrived on.</summary>
internal sealed class ChannelSink(IFrameChannel channel) : IFrameSink
{
    /// <summary>
    /// Where replies to a request received on <paramref name="channel"/> go: the link back to the node that named
    /// itself with a Hello (one ordered stream per direction), else the channel itself (its sink created once).
    /// </summary>
    public static IReplySink For(IFrameChannel channel)
    {
        if (channel.Tag is PeerLink link) return link;
        if (channel.Tag is ChannelSink sink) return sink;
        sink = new ChannelSink(channel);
        channel.Tag = sink;
        return sink;
    }

    public void Send(ReadOnlySpan<byte> frame) => channel.Send(frame);
}
