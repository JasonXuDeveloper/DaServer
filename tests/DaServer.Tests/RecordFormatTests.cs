using DaServer.Persistence;
using System.Collections.Frozen;
using EasyEcs;

namespace DaServer.Tests;

/// <summary>
/// Actor records: a format header, then a section per component. The bytes of a known record are pinned (a Nino upgrade
/// that changes them shows here first), and a record of another format is refused instead of misread.
/// </summary>
public class RecordFormatTests
{
    private static readonly PersistentType[] Types = [new PersistentType<Ledger>(TypeId<Ledger>.Value, static (_, _) => { })]; // stored (its change tracking is not used here)
    private static readonly FrozenDictionary<uint, PersistentType> ById = Types.ToFrozenDictionary(t => t.TypeId);

    /// <summary>"DR", format 1, then Ledger's section: its type id, its length, its version-tolerant Nino bytes.</summary>
    private const string Golden = "445201007D12A494080000000400000007000000";

    private static byte[] Written(World world, int gold)
    {
        using var record = Records.Write(world.CreateEntity(new Ledger(gold)), Types);
        return record.Frame[FrameHeader.Size..].ToArray();
    }

    [Test]
    public void ARecordKeepsItsBytesAndReadsBack()
    {
        using var world = new World();
        var bytes = Written(world, 7);
        Assert.That(Convert.ToHexString(bytes), Is.EqualTo(Golden));

        var loaded = world.CreateEntity();
        Records.Read(loaded, bytes, ById);
        Assert.That(loaded.Get<Ledger>().Gold, Is.EqualTo(7));
    }

    [Test]
    public void ARecordOfAnotherFormatIsRefused()
    {
        using var world = new World();
        var bytes = Written(world, 7);
        bytes[2] = 2; // format 2
        Assert.That(() => Records.Read(world.CreateEntity(), bytes, ById), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("format 2"));
        Assert.That(() => Records.Read(world.CreateEntity(), bytes.AsSpan(4).ToArray(), ById), Throws.InstanceOf<InvalidDataException>());
    }
}
