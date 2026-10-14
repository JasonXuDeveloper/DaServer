using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// World singleton of every shard world: the shard that owns the world. Systems stay plain stateless
/// <see cref="EcsSystem"/>s and reach the shard and the cluster through it (<c>world.Cluster()</c>).
/// </summary>
public readonly record struct ShardContext(Shard Shard) : ISingletonComponent;

/// <summary>Shard and cluster access from ECS code (systems, <c>[ForEach]</c> methods, event handlers).</summary>
public static class ShardWorldExtensions
{
    /// <summary>The shard owning <paramref name="world"/>.</summary>
    public static Shard Shard(this World world) => world.GetSingleton<ShardContext>().Shard;

    /// <summary>
    /// The cluster: generated proxies hang off it (<c>world.Cluster().GameClient(group).OnMoved(..)</c>), plus session
    /// binding and groups.
    /// </summary>
    public static Node Cluster(this World world) => world.GetSingleton<ShardContext>().Shard.Node;
}
