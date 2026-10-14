using Nino.Core;

namespace DaServer.Cluster;

public enum MemberStatus : byte
{
    Joining = 0,
    Up = 1,
    Suspect = 2,
    Leaving = 3,
    Dead = 4,
}

/// <summary>An actor contract hosted by a node, with the implementation that serves it (placement identity).</summary>
[NinoType]
public partial struct HostedActor
{
    public uint ContractId;
    public uint ImplementationId;
}

/// <summary>A cluster event handled by an implementation hosted on a node (event routing).</summary>
[NinoType]
public partial struct HostedHandler
{
    public uint EventId;
    public uint ImplementationId;

    /// <summary>Actor implementation (delivered to the activation of the event key) rather than a service.</summary>
    public bool Actor;
}

/// <summary>Gossiped description of a cluster member. Immutable once published (copy to change).</summary>
[NinoType]
public partial class MemberInfo
{
    public ulong NodeId;
    public string Name = "";
    public string Host = "";
    public int Port;

    /// <summary>Client (gateway) port, 0 when the node accepts no game clients.</summary>
    public int ClientPort;

    /// <summary>Stateless service contracts hosted by the node.</summary>
    public uint[] Services = [];

    /// <summary>Actor implementations (ContractId 0 for an actor that only handles events).</summary>
    public HostedActor[] Actors = [];

    /// <summary>Cluster events handled by the node's implementations.</summary>
    public HostedHandler[] Handlers = [];

    /// <summary>Bumped by the node itself to refute suspicion or announce a restart.</summary>
    public int Incarnation;

    /// <summary>Incremented by the node every gossip round; staleness = failure detection (no clock sync needed).</summary>
    public long Heartbeat;

    public MemberStatus Status;

    /// <summary>Queued messages across the node's shards (load hint for service balancing).</summary>
    public int Load;

    public MemberInfo Clone() => (MemberInfo)MemberwiseClone();

    /// <summary>Gossip merge order: higher incarnation, then worse status, then higher heartbeat wins.</summary>
    public bool Supersedes(MemberInfo other)
    {
        if (Incarnation != other.Incarnation) return Incarnation > other.Incarnation;
        if (Status != other.Status) return Status > other.Status;
        return Heartbeat > other.Heartbeat;
    }

    public override string ToString() => $"{Name}({NodeId:x}) {Host}:{Port} {Status} inc={Incarnation} hb={Heartbeat}";
}
