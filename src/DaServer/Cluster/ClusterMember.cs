namespace DaServer.Cluster;

public enum MemberStatus : byte
{
    Joining = 0,
    Up = 1,
    Suspect = 2,
    Leaving = 3,
    Dead = 4,
}

/// <summary>A message type handled by a handler type hosted on a node (message routing and actor placement).</summary>
[NinoType]
public partial struct HostedMessage
{
    public uint MessageId;
    public uint HandlerTypeId;

    /// <summary>Actor handler type (delivered to the loaded actor of the message key) rather than a service.</summary>
    public bool Actor;
}

/// <summary>
/// Description of a cluster member: its card (identity, endpoints, handled messages: fixed for the node's lifetime, sent
/// once to each node that meets it) and its pulse (<see cref="Pulse"/>, gossiped every round). Incarnation and status
/// changes publish a copy; heartbeat, load and session counts are updated in place, so the view always shows the
/// latest load hints without being rebuilt (readers may see a hint one round old).
/// </summary>
[NinoType]
public partial class ClusterMember
{
    public ulong NodeId;
    public string Name = "";
    public string Host = "";
    public int Port;

    /// <summary>Client (gateway) port, 0 when the node accepts no game clients.</summary>
    public int ClientPort;

    /// <summary>Messages handled by the node's handler types.</summary>
    public HostedMessage[] Handlers = [];

    /// <summary>Address scopes the node's features handle (<see cref="INodeContext.HandleAddress{TAddr}"/>): group messages of a scope go to these nodes.</summary>
    public uint[] Scopes = [];

    /// <summary>Bumped by the node itself to refute suspicion or announce a restart.</summary>
    public int Incarnation;

    /// <summary>Incremented by the node every gossip round; staleness = failure detection (no clock sync needed).</summary>
    public long Heartbeat;

    public MemberStatus Status;

    /// <summary>Queued messages across the node's shards (load hint for service balancing).</summary>
    public int Load;

    /// <summary>Host game clients use to reach this gateway (its public name or address).</summary>
    public string ClientHost = "";

    /// <summary>Connected client sessions (gateways): clients are directed to the least loaded gateway.</summary>
    public int Sessions;

    /// <summary>
    /// Clients this node directed to the gateway since its last pulse (balancers only): added to <see cref="Sessions"/>
    /// so a burst of connections within one gossip round does not all pick the gateway that reported the fewest.
    /// </summary>
    [NinoIgnore]
    internal int Directed;

    /// <summary>Sessions the gateway has, as far as this node knows.</summary>
    internal int ExpectedSessions => Sessions + Volatile.Read(ref Directed);

    /// <summary>A balancer: it directs clients to the other gateways and keeps no sessions itself.</summary>
    public bool Balancer;

    /// <summary>A gateway clients may be directed to.</summary>
    public bool AcceptsSessions => ClientPort != 0 && !Balancer && Status == MemberStatus.Up;

    /// <summary>This node's link to the member (resolved once; copies share it: the endpoint never changes).</summary>
    [NinoIgnore]
    internal PeerLink? Link;

    public ClusterMember Clone() => (ClusterMember)MemberwiseClone();

    /// <summary>Gossip merge order: higher incarnation, then worse status, then higher heartbeat wins.</summary>
    public bool Supersedes(ClusterMember other) => Pulse.Of(this).Supersedes(other);

    public override string ToString() => $"{Name}({NodeId:x}) {Host}:{Port} {Status} inc={Incarnation} hb={Heartbeat}";
}

/// <summary>
/// The part of a member that changes while it runs: what a gossip round carries for every member (29 bytes, written
/// and read in place, so a round allocates nothing). Unknown node ids are answered with full cards.
/// </summary>
internal readonly record struct Pulse(ulong NodeId, int Incarnation, long Heartbeat, MemberStatus Status, int Load, int Sessions)
{
    public const int Size = 8 + 4 + 8 + 1 + 4 + 4;

    public static Pulse Of(ClusterMember m) => new(m.NodeId, m.Incarnation, m.Heartbeat, m.Status, m.Load, m.Sessions);

    /// <summary>Gossip merge order: higher incarnation, then worse status, then higher heartbeat wins.</summary>
    public bool Supersedes(ClusterMember current)
    {
        if (Incarnation != current.Incarnation) return Incarnation > current.Incarnation;
        if (Status != current.Status) return Status > current.Status;
        return Heartbeat > current.Heartbeat;
    }

    public void Write(Span<byte> s)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(s, NodeId);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(8), Incarnation);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(12), Heartbeat);
        s[20] = (byte)Status;
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(21), Load);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(25), Sessions);
    }

    public static Pulse Read(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(s),
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(8)),
        BinaryPrimitives.ReadInt64LittleEndian(s.Slice(12)),
        (MemberStatus)s[20],
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(21)),
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(25)));
}
