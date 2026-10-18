namespace DaServer.Runtime;

/// <summary>
/// World singleton of every shard world: the shard that owns the world. Systems stay plain stateless
/// <see cref="EcsSystem"/>s and reach the shard and the cluster through it (<c>world.Cluster</c>).
/// </summary>
internal readonly record struct ShardContext(Shard Shard) : ISingletonComponent;
