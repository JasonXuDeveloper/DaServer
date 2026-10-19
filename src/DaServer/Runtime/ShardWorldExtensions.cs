namespace DaServer.Runtime;

/// <summary>Shard and cluster access from ECS code (systems, <c>[ForEach]</c> methods, event handlers).</summary>
public static class ShardWorldExtensions
{
    extension(World world)
    {
        /// <summary>The shard owning the world.</summary>
        internal Shard Shard => world.ShardContext.Shard;

        /// <summary>
        /// Ticks the shard has run (at <c>NodeOptions.TickRate</c> per second): the clock of time-based logic, e.g. a
        /// component holding a due tick that a system compares with it.
        /// </summary>
        public long Tick => world.Shard.TickCount;

        /// <summary>The shards' clock (<see cref="NodeOptions.TimeProvider"/>): what tests move on with a manual clock.</summary>
        public TimeProvider Time => world.Shard.Node.Options.TimeProvider;

        /// <summary>
        /// The cluster as systems see it, as actors do: <c>world.Cluster.Send(mapId, new Move(..))</c>, <c>Publish</c>,
        /// pushes and broadcasts.
        /// </summary>
        public ICluster Cluster => world.Shard.Node;

        /// <summary>The node's logger.</summary>
        public ILogger Logger => world.Shard.Node.Logger;
    }
}
