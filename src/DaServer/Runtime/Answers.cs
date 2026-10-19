namespace DaServer.Runtime;

// Asks from handlers (ADR-081): nothing waits. The shard registers a pending call whose reply, refusal or timeout
// becomes the message Answered<TRequest, TReply> to the asker's address: it follows an actor that moved and queues
// behind its mail like any other message. An Answered payload is [call: u32][status: u8][reply to: entity index i32, version
// i32, call u32][reply: Nino bytes].

/// <summary>Asks of handlers and the replies they give later.</summary>
internal static class Answers
{
    private const int HeaderSize = sizeof(uint) + sizeof(byte) + 2 * sizeof(int) + sizeof(uint);

    public static uint Ask<TAddr, TRequest, TReply>(Shard shard, ActorAddress asker, TAddr address, in TRequest request, ReplyTo forward)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply>
    {
        if (Addresses<TAddr>.Kind != AddressKind.Actor) throw new InvalidOperationException($"{typeof(TAddr)} cannot be asked: send to it");
        var call = AnswerCall.Rent(shard, asker, TypeId<Answered<TRequest, TReply>>.Value, forward);
        shard.Node.AskAs(call, MessageWriter.Write(in request), TypeId<TRequest>.Value, Addresses<TAddr>.Scope, Addresses<TAddr>.Value(in address));
        return call.Id;
    }

    public static uint Ask<TRequest, TReply>(Shard shard, ActorAddress asker, in TRequest request, ReplyTo forward) where TRequest : struct, IRequest<TReply>
    {
        var call = AnswerCall.Rent(shard, asker, TypeId<Answered<TRequest, TReply>>.Value, forward);
        shard.Node.AskAs(call, MessageWriter.Write(in request), TypeId<TRequest>.Value, 0, 0);
        return call.Id;
    }

    public static uint ChangeAccess(Shard shard, ActorAddress asker, Entity actor, ClientSession session, AccessChange change)
    {
        if (actor != default) ActorSessions.Change(shard.World, actor, session, change);
        var call = AnswerCall.Rent(shard, asker, TypeId<Answered<SessionChange, Done>>.Value, default);
        shard.Node.ChangeAccess(session, new SessionAccess(asker.HandlerTypeId, asker.Key, change), call);
        return call.Id;
    }

    /// <summary>Reply <paramref name="value"/> to <paramref name="to"/> when the pending call <paramref name="callId"/> ends (its error if it failed).</summary>
    public static void ReplyAfter<T>(Shard shard, uint callId, ReplyTo to, in T value)
    {
        if (!to.Expected) return;
        if (!AnswerCall.TryGet(shard, callId, out var call))
        {
            Replies.Reply(shard.World, to, in value); // it ended already (completed inline)
            return;
        }

        call.ReplyWhenDone(to, MessageWriter.Write(in value));
    }

    /// <summary>The request an <see cref="Answered{TRequest,TReply}"/> payload carries along (default when none or unreadable).</summary>
    public static ReplyTo ForwardOf(ReadOnlySpan<byte> payload) => payload.Length < HeaderSize ? default
        : new ReplyTo(new EntityId(BinaryPrimitives.ReadInt32LittleEndian(payload[5..]), BinaryPrimitives.ReadInt32LittleEndian(payload[9..])),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[13..]));

    /// <summary>Read an <see cref="Answered{TRequest,TReply}"/> payload.</summary>
    public static bool TryRead<TRequest, TReply>(ReadOnlySpan<byte> payload, out Answered<TRequest, TReply> answered)
        where TRequest : struct, IRequest<TReply>
    {
        answered = default;
        if (payload.Length < HeaderSize) return false;
        uint call = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var status = (RpcStatus)payload[sizeof(uint)];
        var forward = ForwardOf(payload);
        TReply value = default!;
        if (status == RpcStatus.Ok && typeof(TReply) != typeof(Done) && !MessageReader.TryRead(payload[HeaderSize..], out value)) return false;
        answered = new Answered<TRequest, TReply>(call, status, value, forward);
        return true;
    }

    /// <summary>Write an answer's payload (the reply's bytes follow when <paramref name="reply"/> is given).</summary>
    public static FrameWriter Write(uint call, RpcStatus status, ReplyTo forward, ReadOnlySpan<byte> reply)
    {
        var w = FrameWriter.Rent(FrameHeader.Size + HeaderSize + reply.Length);
        var span = w.GetSpan(HeaderSize + reply.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span, call);
        span[sizeof(uint)] = (byte)status;
        BinaryPrimitives.WriteInt32LittleEndian(span[5..], forward.Pending.Index);
        BinaryPrimitives.WriteInt32LittleEndian(span[9..], forward.Pending.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[13..], forward.CallId);
        reply.CopyTo(span[HeaderSize..]);
        w.Advance(HeaderSize + reply.Length);
        return w;
    }
}

/// <summary>
/// A handler's ask in flight (pooled per shard): completed on the shard thread, it sends the answer to the asker's
/// address, or replies to the request the asker kept (<see cref="Context.ReplyAfter{T}"/>).
/// </summary>
internal sealed class AnswerCall : IPendingCall
{
    private Shard _shard = null!;
    private ActorAddress _asker;
    private uint _answerId; // the message id of Answered<TRequest, TReply>
    private ReplyTo _replyTo; // ReplyAfter: the request it answers instead
    private ReplyTo _forward; // the request the asker was handling, carried by the answer
    private System.Diagnostics.ActivityContext _trace; // the asking handler's span: the answer's handler continues it
    private FrameWriter? _reply; // and the reply it gives
    private RpcStatus _failed;
    private bool _fail;

    /// <summary>The number <see cref="Context.Ask{TAddr,TRequest,TReply}"/> returned (the shard's own sequence: no collision while in flight).</summary>
    public uint Id { get; private set; }

    public CompletionThread? Caller => _shard;

    public static AnswerCall Rent(Shard shard, ActorAddress asker, uint answerId, ReplyTo forward)
    {
        ref var calls = ref shard.World.AnswerCalls;
        var call = calls.Pool.Count > 0 ? calls.Pool.Pop() : new AnswerCall();
        call._shard = shard;
        call._asker = asker;
        call._answerId = answerId;
        call._forward = forward;
        call._trace = Tracing.Current();
        call.Id = ++calls.Last == 0 ? ++calls.Last : calls.Last;
        calls.InFlight[call.Id] = call;
        return call;
    }

    public static bool TryGet(Shard shard, uint id, out AnswerCall call) =>
        shard.World.AnswerCalls.InFlight.TryGetValue(id, out call!);

    public void ReplyWhenDone(ReplyTo to, FrameWriter reply)
    {
        _replyTo = to;
        _reply?.Dispose();
        _reply = reply;
    }

    /// <summary>The reply arrived (on the shard thread: the node posts completions of a shard's calls to it).</summary>
    public void Complete(RpcStatus status, ReadOnlySpan<byte> payload) => Deliver(status, payload);

    /// <summary>Refused or timed out, on any thread: delivered on the shard thread.</summary>
    public void Fail(RpcStatus status, string? message = null, TimeSpan retryAfter = default)
    {
        if (Shard.Current == _shard)
        {
            Deliver(status, default);
            return;
        }

        _failed = status;
        _fail = true;
        _shard.Post(new WorkItem(WorkKind.Callback, (Action<object?>)DeliverFailure, this));
    }

    private static readonly Action<object?> DeliverFailure = static state =>
    {
        var call = (AnswerCall)state!;
        call.Deliver(call._failed, default);
    };

    private void Deliver(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        var shard = _shard;
        ref var calls = ref shard.World.AnswerCalls;
        if (!calls.InFlight.Remove(Id)) return; // delivered already
        try
        {
            if (_replyTo.Expected) // ReplyAfter: the kept request gets the reply (or this call's failure)
            {
                if (status == RpcStatus.Ok) Replies.Send(shard.World, _replyTo, _reply!);
                else Replies.Fail(shard.World, _replyTo, status);
                _reply = null;
                return;
            }

            var w = Answers.Write(Id, status, _forward, status == RpcStatus.Ok ? payload : default);
            Tracing.Write(w, in _trace);
            if (_asker.HandlerTypeId == 0) shard.Node.SendMessage(w, _answerId, 0, 0); // a service asked
            else shard.Node.SendMessage(w, _answerId, _asker.HandlerTypeId, _asker.Key);
        }
        finally
        {
            _reply?.Dispose();
            (_reply, _replyTo, _forward, _fail, _asker, _trace) = (null, default, default, false, default, default);
            if (calls.Pool.Count < AnswerCalls.MaxPooled) calls.Pool.Push(this);
        }
    }
}

/// <summary>A shard's asks in flight, by the number each ask returned, and the pooled calls.</summary>
internal record struct AnswerCalls() : ISingletonComponent
{
    public const int MaxPooled = 4096;
    public readonly Dictionary<uint, AnswerCall> InFlight = new();
    public readonly Stack<AnswerCall> Pool = new();
    public uint Last;
}

/// <summary>
/// A request whose reply is given later (<see cref="Context.ReplyLater"/>): an entity of the shard's world while its
/// caller waits, so a <see cref="ReplyTo"/> is its <see cref="EntityId"/> (a reused entity has another version: a stale
/// token replies to nobody). Removed when replied, or when its caller stopped waiting (<see cref="ReplySlotSystem"/>).
/// </summary>
internal readonly record struct PendingReply(IReplySink Sink, uint CallId, long Deadline) : IComponent;

/// <summary>Replies given later, through <see cref="PendingReply"/> entities.</summary>
internal static class Replies
{
    public static ReplyTo Hold(World world, IReplySink sink, uint callId, long deadline) =>
        new(world.CreateEntity(new PendingReply(sink, callId, deadline)).Id, callId);

    public static bool Reply<T>(World world, ReplyTo to, in T value)
    {
        if (!TryTake(world, to, out var sink)) return false;
        FrameWriter w;
        try
        {
            w = MessageWriter.Write(in value);
        }
        catch (Exception e)
        {
            sink.SendError(to.CallId, RpcStatus.Error, "Reply serialization failed: " + e.Message);
            return true;
        }

        sink.SendReply(to.CallId, RpcStatus.Ok, w);
        return true;
    }

    /// <summary>Send a written reply (owned: disposed when the caller is gone).</summary>
    public static bool Send(World world, ReplyTo to, FrameWriter reply)
    {
        if (!TryTake(world, to, out var sink))
        {
            reply.Dispose();
            return false;
        }

        sink.SendReply(to.CallId, RpcStatus.Ok, reply);
        return true;
    }

    public static bool Fail(World world, ReplyTo to, RpcStatus status)
    {
        if (!TryTake(world, to, out var sink)) return false;
        sink.SendError(to.CallId, status);
        return true;
    }

    /// <summary>The sink of a waiting request, which stops waiting here (its entity goes).</summary>
    private static bool TryTake(World world, ReplyTo to, out IReplySink sink)
    {
        sink = null!;
        if (!to.Expected || !world.TryGetEntity(to.Pending, out var entity) || !entity.TryGetPendingReply(out var pending) ||
            pending.CallId != to.CallId) return false;
        sink = pending.Sink;
        entity.Destroy();
        return true;
    }
}
