namespace DaServer.Runtime;

// Outgoing messages. To an actor: its id type names the actor type (handler type id), its value the instance; the
// node owning that id is found by rendezvous hashing. To a service: the message id names the one service type handling
// it (gossiped handlers in the ClusterView). Every message is a Message frame whose Target is the handler type id
// (actor or service); Ask adds a call id trailer, answered by a Reply frame.
sealed partial class Node
{
    public void Send<TAddr, T>(TAddr address, in T message) where TAddr : struct, IAddress where T : struct
    {
        var w = MessageWriter.Write(in message);
        switch (Addresses<TAddr>.Kind) // a JIT constant: the other branches vanish
        {
            case AddressKind.Actor:
                SendMessage(w, TypeId<T>.Value, Addresses<TAddr>.Scope, Addresses<TAddr>.Value(in address));
                break;
            case AddressKind.Node:
                SendToNode(w, Addresses<TAddr>.Scope, TypeId<T>.Value, Addresses<TAddr>.Node(in address), Addresses<TAddr>.Value(in address));
                break;
            default:
                SendToGroup(w, Addresses<TAddr>.Scope, TypeId<T>.Value, Addresses<TAddr>.Value(in address));
                break;
        }
    }

    public long SendAfter<TAddr, T>(TimeSpan delay, TAddr address, in T message) where TAddr : struct, IAddress where T : struct =>
        Schedule(delay, new DelayedSend(MessageWriter.Write(in message), Addresses<TAddr>.Kind, Addresses<TAddr>.Scope, TypeId<T>.Value,
            Addresses<TAddr>.Kind == AddressKind.Node ? Addresses<TAddr>.Node(in address) : 0, Addresses<TAddr>.Value(in address)));

    public long SendAfter<T>(TimeSpan delay, in T message) where T : struct =>
        Schedule(delay, new DelayedSend(MessageWriter.Write(in message), AddressKind.Actor, 0, TypeId<T>.Value, 0, 0));

    public bool CancelSend(long timer) => Shard.Current is { } shard && shard.Node == this &&
                                          shard.World.GetSingleton<ShardTimers>().Cancel(shard.Index, timer);

    private long Schedule(TimeSpan delay, in DelayedSend send)
    {
        if (Shard.Current is not { } shard || shard.Node != this)
        {
            send.Frame.Dispose();
            throw NotOnShard();
        }

        return shard.World.GetSingleton<ShardTimers>().Schedule(shard.Index, delay, in send);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException NotOnShard() =>
        new("SendAfter runs on a shard thread (in actors, services and systems): its timers are the shard's data");

    /// <summary>Send a message whose payload is written (a delayed send that is due), by its address kind. Takes ownership of <paramref name="w"/>.</summary>
    internal void Deliver(FrameWriter w, AddressKind kind, uint scope, uint messageId, ulong node, long value)
    {
        switch (kind)
        {
            case AddressKind.Actor:
                SendMessage(w, messageId, scope, value);
                break;
            case AddressKind.Node:
                SendToNode(w, scope, messageId, node, value);
                break;
            default:
                SendToGroup(w, scope, messageId, value);
                break;
        }
    }

    /// <summary>One-way message to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>).</summary>
    internal void SendTo<T>(uint handlerTypeId, long key, in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), TypeId<T>.Value, handlerTypeId, key);

    public void Send<T>(in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), TypeId<T>.Value, handlerTypeId: 0, key: 0);

    public Reply<TReply> Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply> =>
        Addresses<TAddr>.Kind == AddressKind.Actor
            ? Ask<TRequest, TReply>(in request, Addresses<TAddr>.Scope, Addresses<TAddr>.Value(in address))
            : NotAskable<TAddr, TReply>();

    /// <summary>Sessions and groups are sent to, not asked (DAS008 reports it at build time).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Reply<TReply> NotAskable<TAddr, TReply>() =>
        new(ValueTask.FromException<TReply>(new RpcException(RpcStatus.Error, $"{typeof(TAddr)} cannot be asked: send to it")));

    public Reply<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, handlerTypeId: 0, key: 0);

    private Reply<TReply> Ask<TRequest, TReply>(in TRequest request, uint handlerTypeId, long key) where TRequest : struct
    {
        var call = PendingCall<TReply>.Rent(Shard.Current);
        var task = call.Task;
        var frame = MessageWriter.Write(in request);
        long chain = CallChain.Current;
        if (chain != 0) frame.SetChain(chain); // the asking turn's chain (ADR-059)
        Request(frame, TypeId<TRequest>.Value, handlerTypeId, key, call, null, 0);
        return new Reply<TReply>(task);
    }

    /// <summary>
    /// Ask a granted actor to accept a resumed session (<see cref="ActorSessions.Resume"/>): fails with
    /// <see cref="RpcStatus.Forbidden"/> when the actor does not hold the previous session granted.
    /// </summary>
    internal ValueTask Resume(uint handlerTypeId, long key, in SessionResumed resumed)
    {
        var call = PendingCall<NoReply>.Rent(null);
        var task = call.Done;
        Request(MessageWriter.Write(in resumed), TypeId<SessionResumed>.Value, handlerTypeId, key, call, null, 0);
        return task;
    }

    /// <summary>To every service handling the message: serialized once, one frame per service type.</summary>
    public void Publish<T>(in T message) where T : struct
    {
        var routes = View.Routes(TypeId<T>.Value);
        int last = routes.Length - 1;
        while (last >= 0 && routes[last].Actor) last--;
        if (last < 0) return;
        var w = MessageWriter.Write(in message);
        bool handedOver = false;
        try
        {
            for (int i = 0; i <= last; i++)
                if (!routes[i].Actor)
                    handedOver = SendTo(in routes[i], TypeId<T>.Value, 0, w, hops: 0, flags: 0, handOver: i == last);
        }
        finally
        {
            if (!handedOver) w.Dispose();
        }
    }

    /// <summary>
    /// One-way message to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>), or with handler type 0 to the
    /// one service handling <paramref name="messageId"/>. Dropped when nobody hosts the target: one-way delivery is at
    /// most once. Takes ownership of <paramref name="w"/>.
    /// </summary>
    internal void SendMessage(FrameWriter w, uint messageId, uint handlerTypeId, long key, byte flags = 0)
    {
        bool handedOver = false;
        try
        {
            if (TryRoute(messageId, handlerTypeId, out var route, out bool ambiguous))
                handedOver = SendTo(in route, messageId, key, w, hops: 0, flags, handOver: true);
            else if (ambiguous)
                throw new RpcException(RpcStatus.Error, AmbiguousMessage(messageId));
        }
        finally
        {
            if (!handedOver) w.Dispose();
        }
    }

    /// <summary>
    /// Change what a session's client may address (<see cref="Actor.BindSession"/>, <see cref="Actor.GrantSession"/>):
    /// asked of the session, so it completes once the session's node applied it and the client's next message already
    /// reaches the actor.
    /// </summary>
    internal ValueTask ChangeAccess(ClientSession session, SessionAccess access)
    {
        var call = PendingCall<NoReply>.Rent(Shard.Current);
        var task = call.Done;
        AskNode(MessageWriter.Write(in access), AddressScopes.Session, TypeId<SessionAccess>.Value,
            session.Node, session.Id, call);
        return task;
    }

    /// <summary>
    /// A message (payload written) to something node <paramref name="node"/> holds: a client session (by its <see cref="ClientSession"/>
    /// or the <see cref="Origin"/> of its messages), whatever its scope's feature holds. Takes
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

    /// <summary>A message (header written) to an address of this node: to the feature handling its scope, if any.</summary>
    private void DeliverToAddress(ReadOnlySpan<byte> frame, IReplySink? replyTo)
    {
        var header = FrameHeader.Read(frame);
        if (_addressHandlers.TryGetValue(header.Target, out var handler)) handler.OnMessage(in header, frame, replyTo);
    }

    /// <summary>The route of an actor type, or of the one service handling <paramref name="messageId"/> (handler type 0).</summary>
    private bool TryRoute(uint messageId, uint handlerTypeId, out MessageRoute route, out bool ambiguous)
    {
        ambiguous = false;
        if (handlerTypeId != 0) return View.TryGetHandlerTypeRoute(handlerTypeId, out route);
        return View.TryGetRoute(messageId, actor: false, out route, out ambiguous);
    }

    /// <summary>
    /// Send the message in <paramref name="w"/> (payload written) to the node handling <paramref name="route"/>. With
    /// <paramref name="handOver"/> a local target takes <paramref name="w"/> itself instead of a copy; returns whether it
    /// did (the caller disposes <paramref name="w"/> otherwise).
    /// </summary>
    private bool SendTo(in MessageRoute route, uint messageId, long key, FrameWriter w, byte hops, byte flags, bool handOver)
    {
        var target = View.Target(in route, key, Id);
        if (target == null) return false;
        w.SetHeader(FrameKind.Message, route.HandlerTypeId, messageId, key, flags: flags, hops: hops);
        if (target.NodeId != Id)
        {
            GetLink(target).Send(w.Frame);
            return false;
        }

        Admit(SelectShard(route.HandlerTypeId, route.Actor, key), handOver ? w : FrameWriter.CopyOf(w.Frame), null);
        return handOver;
    }

    /// <summary>
    /// Message frame from another node, by its target: a handler type goes to its shard (which forwards an actor this
    /// node does not own), an address scope to the feature handling it, anything else on to the node hosting it.
    /// </summary>
    private void OnInboundMessage(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var handlerType = HandlerTypeById(header.Target);
        if (handlerType != null)
        {
            var copy = FrameWriter.CopyOf(frame);
            IReplySink? sink = null;
            if (header.IsRequest)
            {
                copy.Deadline = header.DeadlineOnArrival();
                sink = ConnectionReplySink.For(connection);
            }

            Admit(SelectShard(handlerType.Id, handlerType.IsActor, header.Key), copy, sink);
        }
        else if (_addressHandlers.TryGetValue(header.Target, out var addresses))
            addresses.OnMessage(in header, frame, header.IsRequest ? ConnectionReplySink.For(connection) : null);
        else Forward(FrameWriter.CopyOf(frame), header.IsRequest ? ConnectionReplySink.For(connection) : null);
    }

    /// <summary>
    /// Re-route a message this node cannot handle (stale view at the sender, ownership moved). Dropped after
    /// <see cref="MaxHops"/> or when nobody hosts its handler type any more (at most once). Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    internal void ForwardMessage(FrameWriter frame)
    {
        bool handedOver = false;
        try
        {
            var header = FrameHeader.Read(frame.Frame);
            if (header.Hops >= MaxHops) return;
            if (View.TryGetHandlerTypeRoute(header.Target, out var route))
                handedOver = SendTo(in route, header.MessageId, header.Key, frame, (byte)(header.Hops + 1), header.Flags, handOver: true);
        }
        finally
        {
            if (!handedOver) frame.Dispose();
        }
    }

    /// <summary>A programming error (a call or send to a message several services handle), reported on a cold path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string AmbiguousMessage(uint messageId) =>
        $"Message 0x{messageId:x8} has several service handlers: Publish it to reach them all";
}
