namespace DaServer.Runtime;

// Outgoing messages. A message type is routed by its id to the implementations handling it (gossiped handlers in the
// ClusterView): Send/Publish as one Message frame per handling implementation (RequestId = implementation id), Ask as
// a Request frame (RequestId = call id; the receiver resolves its implementation by message id and kind).
public sealed partial class Node
{
    public void Send<T>(long key, in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), MessageType<T>.Id, key, actor: true);

    public void Send<T>(in T message) where T : struct =>
        SendMessage(MessageWriter.Write(in message), MessageType<T>.Id, 0, actor: false);

    public ValueTask<TReply> Ask<TRequest, TReply>(long key, in TRequest request) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, key, actor: true);

    public ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(in request, 0, actor: false);

    private ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request, long key, bool actor) where TRequest : struct
    {
        var call = PendingCall<TReply>.Rent(Shard.Current);
        var task = call.Task;
        Request(MessageWriter.Write(in request), MessageType<TRequest>.Id, key, actor, call, null, 0);
        return task;
    }

    public void Publish<TEvent>(in TEvent e) where TEvent : struct => Publish(in e, 0, actors: false);

    public void Publish<TEvent>(long key, in TEvent e) where TEvent : struct => Publish(in e, key, actors: true);

    /// <summary>To every implementation handling the event: services, and actor handlers of <paramref name="key"/>.</summary>
    private void Publish<TEvent>(in TEvent e, long key, bool actors) where TEvent : struct
    {
        var routes = View.Routes(MessageType<TEvent>.Id);
        if (routes.Length == 0) return;
        var w = MessageWriter.Write(in e);
        try
        {
            for (int i = 0; i < routes.Length; i++)
                if (actors || !routes[i].Actor)
                    SendTo(in routes[i], MessageType<TEvent>.Id, key, w, hops: 0, flags: 0);
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>
    /// One-way message to the one implementation of the kind handling <paramref name="messageId"/> (dropped when none
    /// does: one-way delivery is at most once). Takes ownership of <paramref name="w"/>.
    /// </summary>
    internal void SendMessage(FrameWriter w, uint messageId, long key, bool actor, byte flags = 0)
    {
        try
        {
            if (View.TryGetRoute(messageId, actor, out var route, out bool ambiguous))
                SendTo(in route, messageId, key, w, hops: 0, flags);
            else if (ambiguous)
                throw Ambiguous(messageId, actor);
        }
        finally
        {
            w.Dispose();
        }
    }

    /// <summary>Deliver <paramref name="e"/> only to implementation <paramref name="implementationId"/> (runtime notifications).</summary>
    internal void SendTo<TEvent>(uint implementationId, long key, in TEvent e) where TEvent : struct
    {
        var routes = View.Routes(MessageType<TEvent>.Id);
        for (int i = 0; i < routes.Length; i++)
        {
            if (routes[i].ImplementationId != implementationId) continue;
            var w = MessageWriter.Write(in e);
            SendTo(in routes[i], MessageType<TEvent>.Id, key, w, hops: 0, flags: 0);
            w.Dispose();
            return;
        }
    }

    /// <summary>Send the message in <paramref name="w"/> (payload written) to the node handling <paramref name="route"/>.</summary>
    private void SendTo(in MessageRoute route, uint messageId, long key, FrameWriter w, byte hops, byte flags)
    {
        var target = View.Target(in route, key, Id, (uint)Interlocked.Increment(ref _roundRobin));
        if (target == null) return;
        w.SetHeader(FrameKind.Message, route.ImplementationId, messageId, key, flags: flags, hops: hops);
        if (target.NodeId == Id)
            Admit(SelectShard(route.ImplementationId, route.Actor, key), FrameWriter.CopyOf(w.Frame), null);
        else
            GetLink(target).Send(w.Frame);
    }

    /// <summary>Message frame from another node: to the shard of its implementation, or on to the right node.</summary>
    private void OnInboundMessage(in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        var impl = ImplementationById(header.RequestId);
        if (impl == null)
        {
            ForwardMessage(FrameWriter.CopyOf(frame));
            return;
        }

        Admit(SelectShard(impl.Id, impl.IsActor, header.Key), FrameWriter.CopyOf(frame), null);
    }

    /// <summary>
    /// Re-route a message this node cannot handle (stale view at the sender, ownership moved). Dropped after
    /// <see cref="MaxHops"/> or when nobody handles it any more (at most once). Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal void ForwardMessage(FrameWriter frame)
    {
        try
        {
            var header = FrameHeader.Read(frame.Frame);
            if (header.Hops >= MaxHops) return;
            var routes = View.Routes(header.MessageId);
            for (int i = 0; i < routes.Length; i++)
            {
                if (routes[i].ImplementationId != header.RequestId) continue;
                SendTo(in routes[i], header.MessageId, header.Key, frame, (byte)(header.Hops + 1), header.Flags);
                return;
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static InvalidOperationException Ambiguous(uint messageId, bool actor) =>
        new($"Message 0x{messageId:x8} has several {(actor ? "actor" : "service")} handlers: Publish it to reach them all");
}
