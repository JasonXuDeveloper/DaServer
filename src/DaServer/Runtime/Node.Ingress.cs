namespace DaServer.Runtime;

// Ingress (IIngress): messages from outside the cluster enter with an origin and a capped deadline.
sealed partial class Node : IIngress
{
    private IIngressFilter[] _ingressFilters = []; // fixed once the features started
    private long _lastOriginId;

    long INodeContext.NewOriginId() => Interlocked.Increment(ref _lastOriginId);

    IIngress INodeContext.Ingress => this;

    public void Send(uint messageId, uint target, long key, ReadOnlySpan<byte> payload, Origin origin)
    {
        if (Admit(messageId, target, key, origin).Admitted)
            SendMessage(Received(payload, origin), messageId, target, key);
    }

    public void Ask(uint messageId, uint target, long key, ReadOnlySpan<byte> payload, Origin origin, IReplySink replyTo,
        uint callId, TimeSpan budget)
    {
        var decision = Admit(messageId, target, key, origin);
        if (!decision.Admitted)
        {
            replyTo.SendRefusal(callId, decision.Status, decision.RetryAfter);
            return;
        }

        var frame = Received(payload, origin);
        frame.Deadline = DeadlineOf(budget);
        Request(frame, messageId, target, key, null, replyTo, callId, refuseLoaded: true);
    }

    ValueTask<TReply> IIngress.Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request, Origin origin, TimeSpan budget) =>
        Addresses<TAddr>.Kind == AddressKind.Actor
            ? AskFromOutside<TRequest, TReply>(in request, Addresses<TAddr>.Scope, Addresses<TAddr>.Value(in address), origin, budget)
            : NotAskable<TAddr, TReply>().AsValueTask();

    ValueTask<TReply> IIngress.Ask<TRequest, TReply>(in TRequest request, Origin origin, TimeSpan budget) =>
        AskFromOutside<TRequest, TReply>(in request, 0, 0, origin, budget);

    public int LoadOf(ulong nodeId) => View.TryGetMember(nodeId, out var member) ? member.Load : 0;

    private ValueTask<TReply> AskFromOutside<TRequest, TReply>(in TRequest request, uint target, long key, Origin origin, TimeSpan budget)
        where TRequest : struct
    {
        var decision = Admit(MessageType<TRequest>.WireId, target, key, origin);
        if (!decision.Admitted) return Refused<TReply>(decision);
        var call = PendingCall<TReply>.Rent(Shard.Current);
        var task = call.Task;
        var frame = MessageWriter.Write(in request);
        frame.SetOrigin(origin);
        frame.Deadline = DeadlineOf(budget);
        Request(frame, MessageType<TRequest>.WireId, target, key, call, null, 0, refuseLoaded: true);
        return task;
    }

    /// <summary>The ingress filters' verdict: the first refusal, else admitted.</summary>
    private IngressDecision Admit(uint messageId, uint target, long key, Origin origin)
    {
        foreach (var filter in _ingressFilters)
        {
            var decision = filter.Admit(messageId, target, key, origin);
            if (!decision.Admitted) return decision;
        }

        return IngressDecision.Admit;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<TReply> Refused<TReply>(IngressDecision decision) =>
        ValueTask.FromException<TReply>(new RpcException(decision.Status, null, decision.RetryAfter));

    /// <summary>A frame holding a message received from outside (payload as is) and its origin.</summary>
    private static FrameWriter Received(ReadOnlySpan<byte> payload, Origin origin)
    {
        var frame = FrameWriter.Rent(FrameHeader.Size + payload.Length + Origin.Size + sizeof(uint));
        frame.WriteRaw(payload);
        frame.SetOrigin(origin);
        return frame;
    }

    /// <summary>When an outside caller's request stops being worth running: its budget, at most the node's call timeout.</summary>
    private long DeadlineOf(TimeSpan budget)
    {
        long max = (long)Options.CallTimeout.TotalMilliseconds;
        long ms = budget > TimeSpan.Zero ? Math.Min((long)budget.TotalMilliseconds, max) : max;
        return Environment.TickCount64 + Math.Max(1, ms);
    }
}
