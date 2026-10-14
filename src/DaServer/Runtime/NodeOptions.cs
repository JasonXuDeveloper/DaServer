using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>Configuration of a <see cref="Node"/> (bound from the "DaServer" configuration section by the host).</summary>
public sealed class NodeOptions
{
    /// <summary>Human-readable node name (logs, thread names).</summary>
    public string Name { get; set; } = Environment.MachineName;

    /// <summary>
    /// Which registered implementations and systems this node hosts; null (default) = everything compiled into the
    /// process, so the deployment unit is the project. Set it only to run several kinds of nodes in one process
    /// (in-process tests, embedding).
    /// </summary>
    public Func<Type, bool>? Hosts { get; set; }

    /// <summary>Address to bind the cluster (node-to-node) listener to.</summary>
    public string ClusterBind { get; set; } = "0.0.0.0";

    /// <summary>Cluster port; 0 = pick a free port (tests).</summary>
    public int ClusterPort { get; set; }

    /// <summary>Host other nodes use to reach this node (pod IP / DNS name). Defaults to the bind address or loopback.</summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>Client (gateway) port; null = this node accepts no game clients.</summary>
    public int? ClientPort { get; set; }

    /// <summary>Seed endpoints "host:port" (DNS names resolve to every address, e.g. a Kubernetes headless service).</summary>
    public List<string> Seeds { get; set; } = new();

    /// <summary>Number of shards (single-threaded shards); default one per core.</summary>
    public int Shards { get; set; } = Environment.ProcessorCount;

    /// <summary>ECS tick rate of every shard (Hz).</summary>
    public int TickRate { get; set; } = 20;

    /// <summary>Bounded mailbox capacity per shard (overflow spills to an unbounded queue, never drops).</summary>
    public int MailboxCapacity { get; set; } = 64 * 1024;

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan GossipInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SuspectAfter { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan DeadAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>log2 of the max number of concurrent outgoing calls per node.</summary>
    public int PendingCallBits { get; set; } = 18;

    public ILoggerFactory? LoggerFactory { get; set; }

    internal IPEndPoint ClusterBindEndPoint => new(IPAddress.Parse(ClusterBind), ClusterPort);
}
