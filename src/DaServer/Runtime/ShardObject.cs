using DaServer.Rpc;
using EasyEcs;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>
/// What business code (actors, services, shard systems) can reach: its shard, the shard's ECS world and the cluster.
/// Everything here is used from the shard thread only.
/// </summary>
public abstract class ShardObject
{
    /// <summary>The shard this object lives in.</summary>
    public Shard Shard { get; internal set; } = null!;

    /// <summary>The shard's ECS world (shared by every actor, service and system of the shard).</summary>
    public World World => Shard.World;

    /// <summary>
    /// The cluster: generated proxies hang off it (<c>Cluster.Player(id).AddGold(10)</c>,
    /// <c>Cluster.GameClient(session).OnChat(..)</c>), plus session binding and groups.
    /// </summary>
    public Node Cluster => Shard.Node;

    protected ILogger Logger => Shard.Node.Logger;

    /// <summary>
    /// The client session that made the current call (invalid for server-to-server calls). Valid until the handler's
    /// first <c>await</c>: read it into a local first.
    /// </summary>
    protected static SessionRef Caller => CallContext.Current.Caller;
}

/// <summary>
/// Base class of stateless service implementations (<see cref="IService"/> contracts). One instance per shard; a
/// call runs on the shard that received it, so services may cache per-shard data without locks.
/// </summary>
public abstract class Service : ShardObject;
