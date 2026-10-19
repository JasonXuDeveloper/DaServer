using System.Runtime.InteropServices;
namespace DaServer.Cluster;

/// <summary>
/// The coordinator's slot allocation (ADR-079): owners of (actor type, slot) that override rendezvous hashing, with the
/// version that orders tables. Immutable once published; an empty table leaves every slot to rendezvous (ADR-074).
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant)]
internal sealed class SlotTable
{
    public static readonly SlotTable Empty = new();

    public long Version;

    /// <summary>The coordinator that wrote this version.</summary>
    public ulong Coordinator;

    public SlotOverride[] Overrides = [];
}

/// <summary>Slot <see cref="Slot"/> of actor type <see cref="HandlerTypeId"/> is owned by <see cref="Node"/>.</summary>
[NinoType, StructLayout(LayoutKind.Sequential, Pack = 1)] // no padding on the wire
internal readonly record struct SlotOverride(uint HandlerTypeId, ushort Slot, ulong Node);

/// <summary>What a node holds: per actor type, the slots with loaded actors here and how many each (ADR-079).</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
internal sealed class SlotReport
{
    public ulong Node;
    public SlotLoad[] Types = [];
}

/// <summary>An actor type's loaded slots on the reporting node, and whether the type is <see cref="StickyAttribute">sticky</see>.</summary>
[NinoType(format: NinoFormat.VersionTolerant)]
internal sealed class SlotLoad
{
    public uint HandlerTypeId;
    public bool Sticky;
    public ushort[] Slots = [];
    public int[] Counts = [];
}
