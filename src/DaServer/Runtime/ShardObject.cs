namespace DaServer.Runtime;

/// <summary>
/// What business code (actors, services) can reach: the shard's ECS world and the cluster. Everything here is used
/// from the shard thread only. The shard itself (its thread, dispatch queue, callbacks) is not exposed: code that ran outside a
/// turn could interleave with an actor's awaiting turn. Time-based logic is ECS: a component with a due tick and a
/// system comparing it to <see cref="ShardWorldExtensions.Tick"/>.
/// </summary>
public abstract class ShardObject
{
    /// <summary>The shard this object lives in.</summary>
    internal Shard Shard { get; set; } = null!;

    /// <summary>The shard's ECS world (shared by every actor, service and system of the shard).</summary>
    public World World => Shard.World;

    /// <summary>
    /// The cluster: messages (<c>Cluster.Send(mapId, new Move(..))</c>, <c>await Cluster.Ask(id, new GetInfo())</c>),
    /// events, pushes to clients and client groups.
    /// </summary>
    public ICluster Cluster => Shard.Node;

    protected ILogger Logger => Shard.Node.Logger;

    /// <summary>
    /// The client session that sent the current message (invalid for messages from servers). In an actor it
    /// holds for the whole turn, awaits included; in a service (whose calls may overlap) until the first <c>await</c>.
    /// </summary>
    protected SessionRef CallerSession => this is Actor actor ? actor.TurnCaller : CallContext.Current.CallerSession;
}

/// <summary>
/// Base class of stateless services: messages arrive at its [Handle] methods (<c>Cluster.Send(msg)</c>,
/// <c>Cluster.Ask(request)</c>) on any node hosting it (the local node first). One instance per shard; a message runs
/// on the shard that received it, so services may cache per-shard data without locks.
/// </summary>
public abstract class Service : ShardObject;
