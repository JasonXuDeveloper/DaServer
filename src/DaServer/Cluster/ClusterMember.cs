namespace DaServer.Cluster;

public enum MemberStatus : byte
{
    Joining = 0,
    Up = 1,
    Suspect = 2,
    Leaving = 3,
    Dead = 4,
}

/// <summary>
/// The first frame of a node link connection: the protocol and cluster it speaks (refused when either differs), who
/// sends it and which of its streams continues on the connection, with the cluster key's proof. Version-tolerant:
/// fields can be appended without a new link protocol version.
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant)]
internal sealed class LinkHello
{
    public int Version;
    public string Cluster = "";
    public string Host = "";
    public int Port;
    public long StreamId;
    public long At;
    public byte[] Proof = [];
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
/// once to each node that meets it) and its pulse (<see cref="MemberPulse"/>, gossiped every round). Incarnation and status
/// changes publish a copy; heartbeat, load and session counts are updated in place, so the view always shows the
/// latest load hints without being rebuilt (readers may see a hint one round old).
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant)]
public sealed partial class ClusterMember
{
    public ulong NodeId;
    public string Name = "";
    public string Host = "";
    public int Port;

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

    /// <summary>The keys of the node's pulse slots in slot order (counters its features gossip every round).</summary>
    public uint[] PulseKeys = [];

    /// <summary>The pulse slots' values (<see cref="MemberPulse.Slots"/>), updated in place by every gossip round.</summary>
    public int[] PulseValues = new int[MemberPulse.Slots];

    /// <summary>What the node's features publish about it (an endpoint, a mode): set once at start.</summary>
    public CardEntry[] Entries = [];

    /// <summary>
    /// When the node became Up (Unix milliseconds of its own clock): the oldest Up member coordinates slot allocation
    /// (ADR-079). Clocks need not agree closely: any order every node computes alike will do.
    /// </summary>
    public long UpSince;

    /// <summary>The value the member gossips under <paramref name="key"/> (<see cref="INodeContext.AddPulse"/>), 0 when none.</summary>
    public int Pulse(uint key)
    {
        int slot = Array.IndexOf(PulseKeys, key);
        return slot >= 0 && slot < PulseValues.Length ? Volatile.Read(ref PulseValues[slot]) : 0;
    }

    /// <summary>The member's card entry <paramref name="key"/> (<see cref="INodeContext.SetCardEntry"/>), or null.</summary>
    public string? CardEntry(uint key)
    {
        foreach (var entry in Entries)
            if (entry.Key == key) return entry.Value;
        return null;
    }

    /// <summary>The card entry of the member's build (framework and application versions, V2).</summary>
    public static readonly uint BuildKey = KeyOf("daserver.build");

    /// <summary>What the member runs: <c>daserver {version}; app {version}</c>.</summary>
    public string Build => CardEntry(BuildKey) ?? "unknown";

    /// <summary>Whether a feature of the member handles address scope <paramref name="scope"/>.</summary>
    public bool HasScope(uint scope) => Array.IndexOf(Scopes, scope) >= 0;

    /// <summary>A key of pulse slots and card entries: FNV-1a of a name the feature chooses (<c>"clients.sessions"</c>).</summary>
    public static uint KeyOf(string name) => (uint)NinoTypeId.Compute(name);

    /// <summary>This node's link to the member (resolved once; copies share it: the endpoint never changes).</summary>
    [NinoIgnore]
    internal PeerLink? Link;

    public ClusterMember Clone() => (ClusterMember)MemberwiseClone();

    /// <summary>Gossip merge order: higher incarnation, then worse status, then higher heartbeat wins.</summary>
    public bool Supersedes(ClusterMember other) => MemberPulse.Of(this).Supersedes(other);

    public override string ToString() => $"{Name}({NodeId:x}) {Host}:{Port} {Status} inc={Incarnation} hb={Heartbeat}";
}

/// <summary>
/// The part of a member that changes while it runs: what a gossip round carries for every member (37 bytes, written
/// and read in place, so a round allocates nothing). Unknown node ids are answered with full cards.
/// </summary>
internal readonly record struct MemberPulse(ulong NodeId, int Incarnation, long Heartbeat, MemberStatus Status, int Load,
    int Slot0, int Slot1, int Slot2)
{
    /// <summary>Counters features may gossip per node (<see cref="INodeContext.AddPulse"/>).</summary>
    public const int Slots = 3;

    public const int Size = 8 + 4 + 8 + 1 + 4 + Slots * 4;

    public static MemberPulse Of(ClusterMember m)
    {
        var v = m.PulseValues;
        return new(m.NodeId, m.Incarnation, m.Heartbeat, m.Status, m.Load, Slot(v, 0), Slot(v, 1), Slot(v, 2));
    }

    private static int Slot(int[] values, int slot) => slot < values.Length ? Volatile.Read(ref values[slot]) : 0;

    /// <summary>Copy the slots into <paramref name="values"/> (a member's, in place).</summary>
    public void CopySlots(int[] values)
    {
        if (values.Length < Slots) return;
        Volatile.Write(ref values[0], Slot0);
        Volatile.Write(ref values[1], Slot1);
        Volatile.Write(ref values[2], Slot2);
    }

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
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(25), Slot0);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(29), Slot1);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(33), Slot2);
    }

    public static MemberPulse Read(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(s),
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(8)),
        BinaryPrimitives.ReadInt64LittleEndian(s.Slice(12)),
        (MemberStatus)s[20],
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(21)),
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(25)),
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(29)),
        BinaryPrimitives.ReadInt32LittleEndian(s.Slice(33)));
}

/// <summary>A key and value a node's feature publishes on its card (<see cref="ClusterMember.Entries"/>).</summary>
[NinoType]
public partial record struct CardEntry(uint Key, string Value);
