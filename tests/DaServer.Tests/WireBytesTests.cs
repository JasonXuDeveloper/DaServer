using DaServer.Cluster;
using Nino.Core;

namespace DaServer.Tests;

/// <summary>
/// The bytes Nino writes for the shapes the wire carries are pinned: a raw struct, a string, an array, a nested
/// [NinoType], and the framework's own version-tolerant frames (a member card, the slot table). The manifest records
/// layouts as Nino describes them; these catch a Nino upgrade that writes the same layout differently.
/// </summary>
public class WireBytesTests
{
    private static IEnumerable<TestCaseData> Shapes()
    {
        yield return Case(new Point(1, -2), "01000000FEFFFFFF", "a raw struct is its memory");
        yield return Case(new EchoPoint(new Point(3, 4)), "0300000004000000", "a nested raw struct");
        yield return Case(new Fail("hé"), "020000006800E900", "a string");
        yield return Case(new Hop([1, 2], 3, true), "02000000010000000000000002000000000000000300000001", "an array, then raw fields");
        yield return Case(new ClusterMember { NodeId = 7, Name = "a", Host = "h", Port = 9, Incarnation = 2, Heartbeat = 5, Load = 1 },
            "0151000000070000000000000001000000610001000000680009000000000000000000000002000000050000000000000000010000000000000003000000000000000000000000000000000000000000000000000000",
            "a member card");
        yield return Case(new SlotTable { Version = 3, Coordinator = 7, Overrides = [new SlotOverride(11, 2, 7)] },
            "012400000003000000000000000700000000000000010000000B000000020000000700000000000000", "the slot table");
    }

    /// <summary>The value's bytes, and the bytes of what reading them back gives.</summary>
    private static TestCaseData Case<T>(T value, string golden, string name) =>
        new TestCaseData((Func<(byte[], byte[])>)(() =>
        {
            var bytes = NinoSerializer.Serialize(value);
            return (bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(bytes)));
        }), golden).SetName("Pinned: " + name);

    [TestCaseSource(nameof(Shapes))]
    public void TheBytesAreNinosAndReadBack(Func<(byte[] Written, byte[] Reread)> roundTrip, string golden)
    {
        var (written, reread) = roundTrip();
        Assert.That(Convert.ToHexString(written), Is.EqualTo(golden));
        Assert.That(reread, Is.EqualTo(written), "reading the bytes back gives the same value");
    }
}
