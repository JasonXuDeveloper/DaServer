using System.Threading.Tasks;
using DaServer.Rpc;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// Base class of stateful actor implementations (<see cref="IActor"/> contracts). An activation is an entity of its
/// shard's ECS world: keep state in components on <see cref="Entity"/> so shard systems can process every actor of
/// the shard in one pass. Calls run on the shard thread one at a time between awaits (no locks).
/// </summary>
/// <remarks>Refs into components are valid until the next structural change: never keep one across an <c>await</c>.</remarks>
public abstract class Actor : ShardObject
{
    /// <summary>Actor key (player id, map instance id, ...).</summary>
    public long Key { get; internal set; }

    /// <summary>
    /// The activation's client group: sessions that joined it (<c>Cluster.JoinGroup(session, Group)</c>) receive pushes
    /// sent to it (<c>Cluster.PlayerClient(Group).OnMoved(..)</c>). Distinct per implementation and key.
    /// </summary>
    public ClientGroup Group => ClientGroup.Of(Implementation.Id, Key);

    internal ImplementationInfo Implementation { get; set; } = null!;

    /// <summary>Client session of the message whose turn is running (kept across its awaits: turns never interleave).</summary>
    internal SessionRef TurnCaller;

    /// <summary>The activation's entity (carries an <see cref="ActorLink"/> back to this object).</summary>
    public Entity Entity { get; internal set; }

    /// <summary>
    /// The actor is loaded on this node (first message for its key, or its key moved here): read its state from storage
    /// here. Messages wait until it completes.
    /// </summary>
    public virtual ValueTask OnLoad() => default;

    /// <summary>The actor leaves this node (its key moved, the node shuts down): save its state here.</summary>
    public virtual ValueTask OnUnload() => default;

    /// <summary>Components every activation starts with (the entity already has <see cref="ActorLink"/>).</summary>
    internal virtual void InitializeEntity(Entity entity)
    {
    }
}

/// <summary>
/// An actor whose state is one component, added to its entity on load: <c>State.Gold += 10</c>. Systems query it like
/// any component (<c>[ForEach] void Regen(ref PlayerState s)</c> runs over every player of the shard). The state starts
/// as <c>new TState()</c>, so field initializers run: <c>public HashSet&lt;long&gt; Online = new();</c>.
/// </summary>
public abstract class Actor<TState> : Actor where TState : struct, IComponent
{
    /// <summary>The actor's state (a ref into ECS storage: re-read it after an <c>await</c>).</summary>
    protected ref TState State => ref Entity.Get<TState>();

    internal override void InitializeEntity(Entity entity) => entity.Add<TState>() = new TState();
}

/// <summary>Links an actor entity back to its activation (systems can reach the actor object).</summary>
public record struct ActorLink(Actor Actor) : IComponent;
