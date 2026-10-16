using DaServer.Runtime;

namespace DaServer.Testing;

/// <summary>What <see cref="TestCluster.StartAsync"/> starts.</summary>
public sealed class TestClusterOptions
{
    /// <summary>Nodes in the cluster; each hosts everything the test project references unless <see cref="ConfigureNode"/> narrows it.</summary>
    public int Nodes { get; set; } = 1;

    /// <summary>Shards per node.</summary>
    public int Shards { get; set; } = 2;

    /// <summary>Ticks per second of every node (<see cref="NodeOptions.TickRate"/>).</summary>
    public int TickRate { get; set; } = 20;

    /// <summary>
    /// The shards run on a manual clock: no tick, delayed send, idle unload or checkpoint happens until the test moves
    /// the clock (<see cref="TestCluster.TickAsync"/>, <see cref="TestCluster.AdvanceAsync"/>). Off: real time.
    /// </summary>
    public bool ManualTime { get; set; }

    /// <summary>The first node accepts clients (<see cref="TestCluster.ConnectAsync"/>).</summary>
    public bool ClientConnections { get; set; }

    /// <summary>
    /// Adjust a node's options before it starts (its index, its options), e.g. what it hosts:
    /// <c>(i, o) => o.Hosts = t => t.Assembly == (i == 0 ? typeof(Map) : typeof(Player)).Assembly</c>.
    /// </summary>
    public Action<int, NodeOptions>? ConfigureNode { get; set; }
}
