namespace DaServer.Runtime;

/// <summary>
/// What a handler may do, as a value built per message (a <c>ref struct</c>: it cannot be kept). Who is handling
/// (<see cref="Entity"/>, <see cref="Key"/>), who asked (<see cref="Caller"/>), and the cluster's operations: sends,
/// asks whose answers come back as <see cref="Answered{TRequest,TReply}"/> mail, replies given later through a
/// <see cref="ReplyTo"/> kept in a component, and the operations on client sessions.
/// </summary>
public readonly ref struct Context
{
    internal readonly Shard Shard;
    internal readonly ActorAddress Self;
    private readonly IReplySink? _sink;
    private readonly uint _callId;

    internal Context(Shard shard, ActorAddress self, Entity entity, Origin caller, IReplySink? sink, uint callId, long deadline)
    {
        Shard = shard;
        Self = self;
        Entity = entity;
        Caller = caller;
        _sink = sink;
        _callId = callId;
        Deadline = deadline;
    }

    /// <summary>The handling actor's entity (<c>default</c> in a service).</summary>
    public Entity Entity { get; }

    /// <summary>The shard's world.</summary>
    public World World => Shard.World;

    /// <summary>The cluster, for sends that name no handler (to sessions and groups as well as actors and services).</summary>
    public ICluster Cluster => Shard.Node;

    /// <summary>The handling actor's key (0 in a service).</summary>
    public long Key => Self.Key;

    /// <summary>The handling actor's id, of its id type <typeparamref name="TId"/>.</summary>
    public TId Id<TId>() where TId : struct, IActorId => ActorKey.IdOf<TId>(Self);

    /// <summary>The handling actor's client group: the sessions it bound or granted.</summary>
    public ClientGroup Group => ClientGroup.Of(Self.HandlerTypeId, Self.Key);

    /// <summary>
    /// Who sent the message from outside the cluster: a client session (<c>ctx.Cluster.Send(ctx.Caller, m)</c>) or an
    /// HTTP request; invalid for messages sent inside the cluster.
    /// </summary>
    public Origin Caller { get; }

    /// <summary>When the caller stops waiting (<see cref="Platform.Milliseconds"/>), or 0 for one-way mail.</summary>
    public long Deadline { get; }

    /// <summary>Whether a caller waits for a reply to this message.</summary>
    public bool ExpectsReply => _callId != 0 && _sink != null;

    // ---------------------------------------------------------------- sends

    /// <summary>Send <paramref name="message"/> to <paramref name="address"/> (an actor id, a session, a group, the caller).</summary>
    public void Send<TAddr, T>(TAddr address, in T message) where TAddr : struct, IAddress where T : struct =>
        Shard.Node.Send(address, in message);

    /// <summary>Send <paramref name="message"/> to the one service handling it.</summary>
    public void Send<T>(in T message) where T : struct => Shard.Node.Send(in message);

    /// <summary>Publish <paramref name="message"/> to every service handling it.</summary>
    public void Publish<T>(in T message) where T : struct => Shard.Node.Publish(in message);

    /// <summary>Send <paramref name="message"/> to <paramref name="address"/> once <paramref name="delay"/> passed (<see cref="ICluster.SendAfter{TAddr,T}"/>).</summary>
    public long SendAfter<TAddr, T>(TimeSpan delay, TAddr address, in T message) where TAddr : struct, IAddress where T : struct =>
        Shard.Node.SendAfter(delay, address, in message);

    /// <summary>Cancel a delayed send of this shard not sent yet.</summary>
    public bool CancelSend(long timer) => Shard.Node.CancelSend(timer);

    // ---------------------------------------------------------------- asks: the answer is mail

    /// <summary>
    /// Ask <paramref name="address"/>: its reply, refusal or timeout comes back to this actor (or service) as the
    /// message <see cref="Answered{TRequest,TReply}"/>, whose <c>Call</c> is the returned number. Nothing waits: the
    /// handler returns, and the actor takes its next message meanwhile. (Typed overloads are generated per request:
    /// <c>ctx.Ask(mapId, new Enter(..))</c>.) <paramref name="forward"/>: the request this handler replies to later
    /// (<see cref="ReplyLater"/>), carried by the answer (<see cref="Answered{TRequest,TReply}.ReplyTo"/>).
    /// </summary>
    public uint Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request, ReplyTo forward = default)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply> =>
        Answers.Ask<TAddr, TRequest, TReply>(Shard, Self, address, in request, forward);

    /// <summary>Ask the one service handling <paramref name="request"/>; the answer comes back as <see cref="Answered{TRequest,TReply}"/>.</summary>
    public uint Ask<TRequest, TReply>(in TRequest request, ReplyTo forward = default) where TRequest : struct, IRequest<TReply> =>
        Answers.Ask<TRequest, TReply>(Shard, Self, in request, forward);

    // ---------------------------------------------------------------- replies given later

    /// <summary>
    /// The current request's reply is given later (<see cref="Reply{T}(ReplyTo, in T)"/>): keep the returned token in a
    /// component and return <see cref="Answer{T}.Later"/>. A token whose reply never comes ends at the caller's timeout.
    /// </summary>
    public ReplyTo ReplyLater() => ExpectsReply ? Replies.Hold(Shard.World, _sink!, _callId, Deadline) : default;

    /// <summary>Reply <paramref name="value"/> to a request kept with <see cref="ReplyLater"/>; false when its caller is gone.</summary>
    public bool Reply<T>(ReplyTo to, in T value) => Replies.Reply(Shard.World, to, in value);

    /// <summary>Fail a request kept with <see cref="ReplyLater"/> with <paramref name="status"/>.</summary>
    public bool Fail(ReplyTo to, RpcStatus status) => Replies.Fail(Shard.World, to, status);

    /// <summary>
    /// Reply <paramref name="value"/> to the current request once the operation <paramref name="call"/> (an
    /// <see cref="Ask{TAddr,TRequest,TReply}"/> or a session operation) succeeded, or its failure when it failed; the
    /// actor takes its next message meanwhile: <c>return ctx.ReplyAfter(ctx.BindSession(session), true);</c>.
    /// </summary>
    public Answer<T> ReplyAfter<T>(uint call, in T value)
    {
        Answers.ReplyAfter(Shard, call, ReplyLater(), in value);
        return Answer<T>.Later;
    }

    // ---------------------------------------------------------------- client sessions

    /// <summary>
    /// Bind <paramref name="session"/> to this actor: its client's messages naming no actor of this type reach it, and it
    /// joins <see cref="Group"/>. Applied on the session's node; the outcome comes back as
    /// <see cref="Answered{TRequest,TReply}"/> of <see cref="SessionChange"/> (or use <see cref="ReplyAfter{T}"/>).
    /// </summary>
    public uint BindSession(ClientSession session) => Answers.ChangeAccess(Shard, Self, Entity, session, AccessChange.Bind);

    /// <summary>The session has no actor of this type any more; it leaves <see cref="Group"/>.</summary>
    public uint UnbindSession(ClientSession session) => Answers.ChangeAccess(Shard, Self, Entity, session, AccessChange.Unbind);

    /// <summary>The session's client may name this actor (<c>client.Send(id, ..)</c>) and hears <see cref="Group"/>.</summary>
    public uint GrantSession(ClientSession session) => Answers.ChangeAccess(Shard, Self, Entity, session, AccessChange.Grant);

    /// <summary>The session's client may no longer name this actor; it leaves <see cref="Group"/>.</summary>
    public uint RevokeSession(ClientSession session) => Answers.ChangeAccess(Shard, Self, Entity, session, AccessChange.Revoke);

    /// <summary>Close <paramref name="session"/>: its grant of this actor goes at once, then its connection (a kick).</summary>
    public void DisconnectSession(ClientSession session) => ActorSessions.Disconnect(this, session);

    /// <summary>The current message's reply token for the dispatcher (immediate replies).</summary>
    internal CallContext Call => new(_sink, _callId, Caller, Deadline);
}

/// <summary>
/// What a request handler returns: the reply now (<c>return value;</c>), or <see cref="Later"/>, when it is given
/// through a <see cref="ReplyTo"/> taken with <see cref="Context.ReplyLater"/> (or by <see cref="Context.ReplyAfter{T}"/>).
/// </summary>
public readonly struct Answer<T>
{
    private readonly T _value;
    private readonly bool _later;

    private Answer(T value, bool later)
    {
        _value = value;
        _later = later;
    }

    /// <summary>The reply is given later.</summary>
    public static Answer<T> Later => new(default!, true);

    public static implicit operator Answer<T>(T value) => new(value, false);

    internal bool IsLater => _later;

    internal T Value => _value;
}

/// <summary>A session operation of <see cref="Context"/>, as answered (<see cref="Answered{TRequest,TReply}"/> of it).</summary>
public readonly record struct SessionChange(ClientSession Session) : IRequest<Done>;

/// <summary>The reply of a request that answers nothing but its success.</summary>
public readonly struct Done;
