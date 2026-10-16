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
    /// The actor's client group: sessions bound or granted to it (<see cref="BindSession"/>, <see cref="GrantSession"/>)
    /// or that joined it receive
    /// messages sent to it (<c>Cluster.Send(Group, new Moved(..))</c>). Distinct per actor type and key.
    /// </summary>
    public ClientGroup Group => ClientGroup.Of(HandlerType.Id, Key);

    /// <summary>
    /// Bind a client session to this actor: the client's messages handled by this actor type that name no actor reach
    /// this one (a session has one actor per type; binding another replaces it), and the session is granted this
    /// actor (<see cref="GrantSession"/>). Completes once the session's node applied it, so the client's next message
    /// already arrives here.
    /// </summary>
    protected Reply BindSession(ClientSession session) => ChangeAccess(session, AccessChange.Bind);

    /// <summary>Remove the session's binding to this actor and its grant (it also leaves <see cref="Group"/>).</summary>
    protected Reply UnbindSession(ClientSession session) => ChangeAccess(session, AccessChange.Unbind);

    /// <summary>
    /// Grant a client session this actor: its client may name it (<c>client.Send(roomId, new Say(..))</c>; a client
    /// can name only actors granted to its session, anything else is refused at the edge without reaching an actor),
    /// the session joins <see cref="Group"/>, and the actor receives <see cref="SessionClosed"/> when the session
    /// ends and <see cref="SessionResumed"/> when it resumes. The actor decides who may address it, in one place (a
    /// room admitting a player); grants are bounded per session (<see cref="RpcStatus.Overloaded"/> beyond).
    /// Completes once the session's node applied it.
    /// </summary>
    protected Reply GrantSession(ClientSession session) => ChangeAccess(session, AccessChange.Grant);

    /// <summary>Withdraw the session's grant of this actor (a binding to it goes too; it also leaves <see cref="Group"/>).</summary>
    protected Reply RevokeSession(ClientSession session) => ChangeAccess(session, AccessChange.Revoke);

    private Reply ChangeAccess(ClientSession session, AccessChange change)
    {
        ActorSessions.Change(this, session, change);
        return new Reply(Shard.Node.ChangeAccess(session, new SessionAccess(HandlerType.Id, Key, change)));
    }

    internal HandlerTypeInfo HandlerType { get; set; } = null!;

    /// <summary>Origin of the message whose turn is running (kept across its awaits: turns never interleave).</summary>
    internal Origin TurnCaller;

    /// <summary>
    /// Call chain of the turn running now (0: none): a request carrying it would wait for this turn, it fails with
    /// <see cref="RpcStatus.CallCycle"/> (ADR-059).
    /// </summary>
    internal long TurnChain;

    /// <summary>The actor's entity (carries an <see cref="ActorRef"/> back to this object).</summary>
    public Entity Entity { get; internal set; }

    /// <summary>
    /// The actor is loaded on this node (first message for its key, or its key moved here): read its state from storage
    /// here. Messages wait until it completes.
    /// </summary>
    public virtual Reply OnLoad() => default;

    /// <summary>
    /// The actor leaves this node (its key moved, it was idle for <see cref="UnloadAfterIdle"/>, the node shuts down):
    /// save its state here.
    /// </summary>
    public virtual Reply OnUnload() => default;

    /// <summary>
    /// Unload the actor after it received no message for this long (zero, the default: stay loaded until the key moves
    /// or the node stops). A later message loads it again (<see cref="OnLoad"/>), so this bounds the memory of actors
    /// that are only used now and then (offline players, finished matches). Read once when the actor loads.
    /// </summary>
    public virtual TimeSpan UnloadAfterIdle => TimeSpan.Zero;

    /// <summary>
    /// With a store (<see cref="NodeOptions.ActorStore"/>): the actor's <see cref="PersistAttribute">[Persist]</see>
    /// components are written at most this long after they changed (a crash loses at most this much), besides on
    /// unload. Zero: only on unload. A critical change (a trade, a purchase) writes at once: add <see cref="SaveNow"/>.
    /// Read once when the actor loads.
    /// </summary>
    public virtual TimeSpan SaveEvery => TimeSpan.FromSeconds(30);

    /// <summary>
    /// A handler of this actor failed (threw, or its reply failed): what to do with the actor (C4). <see cref="ErrorAction.Resume"/>
    /// (default) keeps it and takes the next message; <see cref="ErrorAction.Reload"/> drops it without saving, so the
    /// next message loads it again from storage (its state may be inconsistent after a half-applied change). The caller
    /// gets the error either way. Runs on the shard thread; keep it a decision, not work.
    /// </summary>
    public virtual ErrorAction OnError(Exception error) => ErrorAction.Resume;

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

/// <summary>What happens to an actor whose handler failed (<see cref="Actor.OnError"/>).</summary>
public enum ErrorAction
{
    /// <summary>Keep the actor and its state; the next message runs.</summary>
    Resume,

    /// <summary>Remove the actor without saving; the next message loads it again.</summary>
    Reload,
}

/// <summary>
/// Tag: write the actor's record now (ADR-068), for a change that must survive a crash (a trade, a purchase):
/// <c>Entity.Add&lt;SaveNow&gt;()</c>. Removed once the write is queued; an unchanged record is not written.
/// </summary>
public struct SaveNow : IComponent;

