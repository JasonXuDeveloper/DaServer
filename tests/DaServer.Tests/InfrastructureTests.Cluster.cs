namespace DaServer.Tests;

/// <summary>Placement, membership cards, node options, addresses and type ids.</summary>
public partial class InfrastructureTests
{
    [Test]
    public void RendezvousPlacementIsStableAndMinimallyDisruptive()
    {
        static ClusterMember Member(ulong id) => Views.Member(id, Views.Handles(70, true, 7));

        var three = new ClusterView([Member(1), Member(2), Member(3)]);
        var four = new ClusterView([Member(1), Member(2), Member(3), Member(4)]);

        int moved = 0;
        for (long key = 0; key < 10_000; key++)
        {
            var before = three.HandlerTypeOwner(70, key)!.NodeId;
            var after = four.HandlerTypeOwner(70, key)!.NodeId;
            Assert.That(three.HandlerTypeOwner(70, key)!.NodeId, Is.EqualTo(before), "deterministic");
            if (before != after)
            {
                moved++;
                Assert.That(after, Is.EqualTo(4UL), "keys only move to the new node");
            }
        }

        Assert.That(moved, Is.InRange(2000, 3000), "about 1/4 of the keys move to the 4th node");
    }

    [Test]
    public void PulsesRoundTrip()
    {
        var pulse = new MemberPulse(0xDEADBEEF12345678, 7, 1L << 40, MemberStatus.Suspect, 123, 4567, -1, int.MaxValue);
        Span<byte> wire = stackalloc byte[MemberPulse.Size];
        pulse.Write(wire);
        Assert.That(MemberPulse.Read(wire), Is.EqualTo(pulse));
    }

    [Test]
    public void MemberMergeOrder()
    {
        var a = new ClusterMember { Incarnation = 1, Heartbeat = 5, Status = MemberStatus.Up };
        Assert.That(new ClusterMember { Incarnation = 2, Heartbeat = 0, Status = MemberStatus.Up }.Supersedes(a), Is.True);
        Assert.That(new ClusterMember { Incarnation = 1, Heartbeat = 0, Status = MemberStatus.Dead }.Supersedes(a), Is.True);
        Assert.That(new ClusterMember { Incarnation = 1, Heartbeat = 6, Status = MemberStatus.Up }.Supersedes(a), Is.True);
        Assert.That(new ClusterMember { Incarnation = 1, Heartbeat = 4, Status = MemberStatus.Up }.Supersedes(a), Is.False);
    }

    [Test]
    public void OptionsANodeCannotRunWithAreRefusedAtOnce()
    {
        var error = Assert.Throws<ArgumentException>(() => new Node(new NodeOptions
            { TickRate = 0, SuspectAfter = TimeSpan.FromSeconds(5), DeadAfter = TimeSpan.FromSeconds(2) }));
        Assert.That(error!.Message, Does.Contain("TickRate 0").And.Contain("below DeadAfter"), "every problem in one message");
    }

    [TestCase("game-0:7000", "game-0", 7000)]
    [TestCase("10.0.0.5:7000", "10.0.0.5", 7000)]
    [TestCase("[fd00::5]:7000", "fd00::5", 7000)]
    public void HostPortParses(string text, string host, int port)
    {
        Assert.That(DaServer.Net.HostPort.TryParse(text, out var h, out int p), Is.True);
        Assert.That((h, p), Is.EqualTo((host, port)));
    }

    [TestCase("game-0")]
    [TestCase("game-0:")]
    [TestCase(":7000")]
    [TestCase("game-0:70000")]
    [TestCase("fe80::1")]
    [TestCase("fd00::5:7000")]
    public void HostPortRefuses(string text) => Assert.That(DaServer.Net.HostPort.TryParse(text, out _, out _), Is.False);

    [Test]
    public void TypeIdsAreNinosTypeIds()
    {
        // The analyzer checks collisions with the same hash at compile time; the runtime and Nino must agree.
        Assert.That(TypeId<Add>.Value, Is.EqualTo((uint)Nino.Core.NinoTypeId.Compute("DaServer.Tests.Add")));
        Assert.That(TypeId<SessionClosed>.Value, Is.EqualTo((uint)Nino.Core.NinoTypeId.Of(typeof(SessionClosed))));
        // A renamed type keeps its identity (and its stored records) with [NinoFormerName].
        Assert.That(TypeId<Renamed>.Value, Is.EqualTo((uint)Nino.Core.NinoTypeId.Compute("Game.Before.Rename")));
    }

    [Nino.Core.NinoFormerName("Game.Before.Rename")]
    private struct Renamed;
}
