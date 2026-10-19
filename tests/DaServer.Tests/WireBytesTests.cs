using DaServer.Cluster;
using Nino.Core;

namespace DaServer.Tests;

/// <summary>
/// The bytes Nino writes for the shapes the wire carries are pinned: a raw struct, strings (one byte a char when they fit
/// Latin-1, else UTF-16), an array, a nested [NinoType], and the framework's own version-tolerant frames (a member card,
/// the slot table, its overrides packed without padding). The manifest records
/// layouts as Nino describes them; these catch a Nino upgrade that writes the same layout differently.
/// </summary>
public class WireBytesTests
{
    private static IEnumerable<TestCaseData> Shapes()
    {
        yield return Case(new Point(1, -2), "01000000FEFFFFFF", "a raw struct is its memory");
        yield return Case(new EchoPoint(new Point(3, 4)), "0300000004000000", "a nested raw struct");
        yield return Case(new Fail("hé"), "FDFFFFFF68E9", "a Latin-1 string, one byte a char");
        yield return Case(new Fail("名字"), "020000000D54575B", "any other string, UTF-16");
        yield return Case(new Hop([1, 2], 3, true), "02000000010000000000000002000000000000000300000001", "an array, then raw fields");
        yield return Case(new ClusterMember { NodeId = 7, Name = "a", Host = "h", Port = 9, Incarnation = 2, Heartbeat = 5, Load = 1 },
            "014F0000000700000000000000FEFFFFFF61FEFFFFFF6809000000000000000000000002000000050000000000000000010000000000000003000000000000000000000000000000000000000000000000000000",
            "a member card");
        yield return Case(new SlotTable { Version = 3, Coordinator = 7, Overrides = [new SlotOverride(11, 2, 7)] },
            "012200000003000000000000000700000000000000010000000B00000002000700000000000000", "the slot table");
    }

    /// <summary>The value's bytes, and the bytes of what reading them back gives.</summary>
    private static TestCaseData Case<T>(T value, string golden, string name) =>
        new TestCaseData((Func<(byte[], byte[])>)(() =>
        {
            var bytes = NinoSerializer.Serialize(value);
            return (bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(bytes)));
        }), golden).SetName("Pinned: " + name);

    [Test]
    public void AStringWrittenAsUtf16ByAnEarlierNinoStillReads()
    {
        // Nino before 4.0.0-rc.6 wrote every string as UTF-16: stored records of that time still read.
        Assert.That(NinoDeserializer.Deserialize<Fail>(Convert.FromHexString("020000006800E900")).Message, Is.EqualTo("hé"));
    }

    [TestCaseSource(nameof(Shapes))]
    public void TheBytesAreNinosAndReadBack(Func<(byte[] Written, byte[] Reread)> roundTrip, string golden)
    {
        var (written, reread) = roundTrip();
        Assert.That(Convert.ToHexString(written), Is.EqualTo(golden));
        Assert.That(reread, Is.EqualTo(written), "reading the bytes back gives the same value");
    }
}
