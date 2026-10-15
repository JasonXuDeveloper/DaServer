namespace DaServer.Runtime;

/// <summary>
/// World singleton of every shard world: the shard that owns the world. Systems stay plain stateless
/// <see cref="EcsSystem"/>s and reach the shard and the cluster through it (<c>world.Cluster()</c>).
/// </summary>
internal readonly record struct ShardContext(Shard Shard) : ISingletonComponent;

/// <summary>Shard and cluster access from ECS code (systems, <c>[ForEach]</c> methods, event handlers).</summary>
public static class ShardWorldExtensions
{
    /// <summary>The shard owning <paramref name="world"/>.</summary>
    internal static Shard Shard(this World world) => world.GetSingleton<ShardContext>().Shard;

    /// <summary>
    /// Ticks the shard has run (at <c>NodeOptions.TickRate</c> per second): the clock of time-based logic, e.g. a
    /// component holding a due tick that a system compares with it.
    /// </summary>
    public static long Tick(this World world) => world.Shard().TickCount;

    /// <summary>
    /// The cluster as systems see it: <c>world.Cluster().Send(mapId, new Move(..))</c>, <c>Publish</c>, pushes and
    /// broadcasts.
    /// </summary>
    public static ICluster Cluster(this World world) => world.Shard().Node;
}
