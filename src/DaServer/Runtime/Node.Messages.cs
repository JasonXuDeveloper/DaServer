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
                SendMessage(w, MessageType<T>.Id, Addresses<TAddr>.Scope, Addresses<TAddr>.Value(in address));
                break;
            case AddressKind.Node:
                SendToNode(w, Addresses<TAddr>.Scope, MessageType<T>.Id, Addresses<TAddr>.Node(in address), Addresses<TAddr>.Value(in address));
                break;
            default:
                SendToGroup(w, Addresses<TAddr>.Scope, MessageType<T>.Id, Addresses<TAddr>.Value(in address));
                break;
        }
    }

    /// <summary>One-way message to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>).</summary>
    internal void SendTo<T>(uint handlerTypeId, long key, in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), MessageType<T>.Id, handlerTypeId, key);

    public void Send<T>(in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), MessageType<T>.Id, handlerTypeId: 0, key: 0);

    public ValueTask<TReply> Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply> =>
        Addresses<TAddr>.Kind == AddressKind.Actor
            ? Ask<TRequest, TReply>(in request, Addresses<TAddr>.Scope, Addresses<TAddr>.Value(in address))
            : NotAskable<TAddr, TReply>();

    /// <summary>Sessions and groups are sent to, not asked (DAS008 reports it at build time).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<TReply> NotAskable<TAddr, TReply>() =>
        ValueTask.FromException<TReply>(new RpcException(RpcStatus.Error, $"{typeof(TAddr)} cannot be asked: send to it"));

    public ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, handlerTypeId: 0, key: 0);

    private ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request, uint handlerTypeId, long key) where TRequest : struct
    {
        var call = PendingCall<TReply>.Rent(Shard.Current);
        var task = call.Task;
        Request(MessageWriter.Write(in request), MessageType<TRequest>.Id, handlerTypeId, key, call, null, 0);
        return task;
    }

    /// <summary>To every service handling the message: serialized once, one frame per service type.</summary>
    public void Publish<T>(in T e) where T : struct
    {
        var routes = View.Routes(MessageType<T>.Id);
        int last = routes.Length - 1;
        while (last >= 0 && routes[last].Actor) last--;
        if (last < 0) return;
        var w = MessageWriter.Write(in e);
        bool handedOver = false;
        try
        {
            for (int i = 0; i <= last; i++)
                if (!routes[i].Actor)
                    handedOver = SendTo(in routes[i], MessageType<T>.Id, 0, w, hops: 0, flags: 0, handOver: i == last);
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
        var target = View.Target(in route, key, Id, (uint)Interlocked.Increment(ref _roundRobin));
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
    /// node does not own), a session or client group to the gateway, anything else on to the node hosting it.
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
        else if (header.Target == AddressScopes.Session || header.Target == AddressScopes.Group) DeliverToAddress(header.Target, header.Key, frame);
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
