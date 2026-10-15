namespace DaServer.Runtime;

/// <summary>
/// Base of every actor (derive from <see cref="Actor{TId}"/> or <see cref="Actor{TId,TState}"/>): one loaded actor per
/// id in the cluster, placed by rendezvous hashing over the nodes hosting the type, loaded on demand, one message at a
/// time. Messages arrive at its [Handle] methods (<c>Cluster.Send(mapId, msg)</c>, <c>Cluster.Ask(mapId, request)</c>).
/// A loaded actor is an entity of its shard's ECS world: state lives in components on <see cref="Entity"/>, so shard
/// systems process every actor of the shard in one pass. Messages run on the shard thread one at a time, awaits
/// included (no locks).
/// </summary>
/// <remarks>Refs into components are valid until the next structural change: never keep one across an <c>await</c>.</remarks>
public abstract class Actor : ShardObject
{
    private protected Actor()
    {
    }

    /// <summary>The value of the actor's id (<see cref="Actor{TId}.Id"/>).</summary>
    internal long Key { get; set; }

    /// <summary>
    /// The actor's client group: sessions bound to it (<see cref="BindSession"/>) or that joined it receive
    /// messages sent to it (<c>Cluster.Send(Group, new Moved(..))</c>). Distinct per actor type and key.
    /// </summary>
    public ClientGroup Group => ClientGroup.Of(HandlerType.Id, Key);

    /// <summary>
    /// Bind a client session to this actor: the client's messages handled by this actor type reach this key (the
    /// client cannot choose another one), the session joins <see cref="Group"/>, and the actor receives
    /// <see cref="SessionClosed"/> when the session ends. Completes once the session's gateway applied it, so the
    /// client's next message already arrives here. A session has at most one key per actor type.
    /// </summary>
    protected ValueTask BindSession(SessionRef session)
    {
        ShardDispatch.RecordSession(this, session);
        return Shard.Node.BindSession(session, HandlerType.Id, Key, bind: true);
    }

    /// <summary>Remove the session's binding to this actor type (it also leaves <see cref="Group"/>).</summary>
    protected ValueTask UnbindSession(SessionRef session) => Shard.Node.BindSession(session, HandlerType.Id, Key, bind: false);

    internal HandlerTypeInfo HandlerType { get; set; } = null!;

    /// <summary>Client session of the message whose turn is running (kept across its awaits: turns never interleave).</summary>
    internal SessionRef TurnCaller;

    /// <summary>The actor's entity (carries an <see cref="ActorRef"/> back to this object).</summary>
    public Entity Entity { get; internal set; }

    /// <summary>
    /// The actor is loaded on this node (first message for its key, or its key moved here): read its state from storage
    /// here. Messages wait until it completes.
    /// </summary>
    public virtual ValueTask OnLoad() => default;

    /// <summary>
    /// The actor leaves this node (its key moved, it was idle for <see cref="UnloadAfterIdle"/>, the node shuts down):
    /// save its state here.
    /// </summary>
    public virtual ValueTask OnUnload() => default;

    /// <summary>
    /// Unload the actor after it received no message for this long (zero, the default: stay loaded until the key moves
    /// or the node stops). A later message loads it again (<see cref="OnLoad"/>), so this bounds the memory of actors
    /// that are only used now and then (offline players, finished matches). Read once when the actor loads.
    /// </summary>
    public virtual TimeSpan UnloadAfterIdle => TimeSpan.Zero;

    /// <summary>Components every loaded actor starts with (the entity already has <see cref="ActorRef"/>).</summary>
    internal virtual void InitializeEntity(Entity entity)
    {
    }
}

/// <summary>An actor addressed by <typeparamref name="TId"/> (<c>Room : Actor&lt;RoomId&gt;</c>).</summary>
public abstract class Actor<TId> : Actor where TId : struct, IActorId
{
    /// <summary>The actor's id (player id, map instance id, ...).</summary>
    public TId Id => ActorIds<TId>.Of(Key);
}

/// <summary>
/// An actor whose state is one component, added to its entity on load: <c>State.Gold += 10</c>. Systems query it like
/// any component (<c>[ForEach] void Regen(ref PlayerState s)</c> runs over every player of the shard). The state starts
/// as <c>new TState()</c>, so field initializers run: <c>public HashSet&lt;long&gt; Online = new();</c>.
/// </summary>
public abstract class Actor<TId, TState> : Actor<TId> where TId : struct, IActorId where TState : struct, IComponent
{
    /// <summary>The actor's state (a ref into ECS storage: re-read it after an <c>await</c>).</summary>
    protected ref TState State => ref Entity.Get<TState>();

    internal override void InitializeEntity(Entity entity) => entity.Add<TState>() = new TState();
}

/// <summary>
/// The loaded actor an actor entity belongs to: systems read its id and client group from it
/// (<c>[ForEach] static void Send(ref MapGrid grid, in ActorRef self, World world) => .. self.Id&lt;MapId&gt;() ..</c>).
/// </summary>
public readonly record struct ActorRef(Actor Actor) : IComponent
{
    /// <summary>The actor's id, of its id type <typeparamref name="TId"/>.</summary>
    public TId Id<TId>() where TId : struct, IActorId =>
        Actor.HandlerType.Id == ActorIds<TId>.HandlerTypeId ? ActorIds<TId>.Of(Actor.Key) : throw WrongIdType(typeof(TId));

    /// <summary>The actor's client group (<see cref="Runtime.Actor.Group"/>).</summary>
    public ClientGroup Group => Actor.Group;

    internal ActorAddress Address => new(Actor.HandlerType.Id, Actor.Key);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private InvalidOperationException WrongIdType(Type type) => new($"{Actor.HandlerType} is not addressed by {type}");
}
