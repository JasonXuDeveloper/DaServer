using EasyEcs.Core;

namespace DaServer.Runtime;

/// <summary>
/// Optional base class of stateless service implementations: one instance per shard, all calls on that shard's
/// thread. Gives access to the shard, its ECS world and the cluster runtime.
/// </summary>
public abstract class Service : IShardAware
{
    public Shard Shard { get; private set; } = null!;

    public World World => Shard.World;

    /// <summary>Cluster runtime: proxies to actors/services, session binding, pushes and broadcasts.</summary>
    public Node Runtime => Shard.Node;

    void IShardAware.Attach(Shard shard) => Shard = shard;
}
