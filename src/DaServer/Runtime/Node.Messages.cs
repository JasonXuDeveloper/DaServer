namespace DaServer.Runtime;

// Outgoing messages. To an actor: its id type names the actor type (handler type id), its value the instance; the
// node owning that id is found by rendezvous hashing. To a service: the message id names the one service type handling
// it (gossiped handlers in the ClusterView). Send and Publish travel as Message frames (RequestId = handler type id),
// Ask as a Request frame (RequestId = call id; an actor request carries its handler type id as a trailer).
sealed partial class Node
{
    public void Send<TId, T>(TId id, in T message) where TId : struct, IActorId where T : struct =>
        SendTo(ActorIds<TId>.HandlerTypeId, id.Value, in message);

    /// <summary>One-way message to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>).</summary>
    internal void SendTo<T>(uint handlerTypeId, long key, in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), MessageType<T>.Id, handlerTypeId, key);

    public void Send<T>(in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), MessageType<T>.Id, handlerTypeId: 0, key: 0);

    public ValueTask<TReply> Ask<TId, TRequest, TReply>(TId id, in TRequest request)
        where TId : struct, IActorId where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, ActorIds<TId>.HandlerTypeId, id.Value);

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
        if (routes.Length == 0) return;
        var w = MessageWriter.Write(in e);
        try
        {
            for (int i = 0; i < routes.Length; i++)
                if (!routes[i].Actor)
                    SendTo(in routes[i], MessageType<T>.Id, 0, w, hops: 0, flags: 0);
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>
    /// One-way message to actor (<paramref name="handlerTypeId"/>, <paramref name="key"/>), or with handler type 0 to the
    /// one service handling <paramref name="messageId"/>. Dropped when nobody hosts the target: one-way delivery is at
    /// most once. Takes ownership of <paramref name="w"/>.
    /// </summary>
    internal void SendMessage(FrameWriter w, uint messageId, uint handlerTypeId, long key, byte flags = 0)
    {
        try
        {
            if (TryRoute(messageId, handlerTypeId, out var route, out bool ambiguous))
                SendTo(in route, messageId, key, w, hops: 0, flags);
            else if (ambiguous)
                throw new RpcException(RpcStatus.Error, AmbiguousMessage(messageId));
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>The route of an actor type, or of the one service handling <paramref name="messageId"/> (handler type 0).</summary>
    private bool TryRoute(uint messageId, uint handlerTypeId, out MessageRoute route, out bool ambiguous)
    {
        ambiguous = false;
        if (handlerTypeId != 0) return View.TryGetHandlerTypeRoute(handlerTypeId, out route);
        return View.TryGetRoute(messageId, actor: false, out route, out ambiguous);
    }

    /// <summary>Send the message in <paramref name="w"/> (payload written) to the node handling <paramref name="route"/>.</summary>
    private void SendTo(in MessageRoute route, uint messageId, long key, FrameWriter w, byte hops, byte flags)
    {
        var target = View.Target(in route, key, Id, (uint)Interlocked.Increment(ref _roundRobin));
        if (target == null) return;
        w.SetHeader(FrameKind.Message, route.HandlerTypeId, messageId, key, flags: flags, hops: hops);
        if (target.NodeId == Id)
            Admit(SelectShard(route.HandlerTypeId, route.Actor, key), FrameWriter.CopyOf(w.Frame), null);
        else
            GetLink(target).Send(w.Frame);
    }

    /// <summary>Message frame from another node: to the shard of its handler type, or on to the right node.</summary>
    private void OnInboundMessage(in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var handlerType = HandlerTypeById(header.RequestId);
        if (handlerType == null)
        {
            ForwardMessage(FrameWriter.CopyOf(frame));
            return;
        }

        Admit(SelectShard(handlerType.Id, handlerType.IsActor, header.Key), FrameWriter.CopyOf(frame), null);
    }

    /// <summary>
    /// Re-route a message this node cannot handle (stale view at the sender, ownership moved). Dropped after
    /// <see cref="MaxHops"/> or when nobody hosts its handler type any more (at most once). Takes ownership of
    /// <paramref name="frame"/>.
    /// </summary>
    internal void ForwardMessage(FrameWriter frame)
    {
        try
        {
            var header = FrameHeader.Read(frame.Frame);
            if (header.Hops >= MaxHops) return;
            if (View.TryGetHandlerTypeRoute(header.RequestId, out var route))
                SendTo(in route, header.MessageId, header.Key, frame, (byte)(header.Hops + 1), header.Flags);
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>A programming error (a call or send to a message several services handle), reported on a cold path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string AmbiguousMessage(uint messageId) =>
        $"Message 0x{messageId:x8} has several service handlers: Publish it to reach them all";
}
