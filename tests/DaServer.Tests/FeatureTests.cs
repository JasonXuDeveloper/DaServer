namespace DaServer.Tests;

/// <summary>Feature points (ADR-048): a toy feature with its own address types, on some nodes of a cluster.</summary>
public class FeatureTests : ClusterTest
{
    [Test]
    public async Task AddressMessagesReachTheFeatureOfTheirNodeAndGroupsReachEveryNodeHandlingThem()
    {
        ProbeFeature a = new(), b = new();
        var nodeA = await Start("a", configure: o => o.Features.Add(a));
        var nodeB = await Start("b", configure: o => o.Features.Add(b));
        var plain = await Start("plain");
        await Converged();
        Assert.That(a.ShardsConfigured, Is.EqualTo(nodeA.Shards.Count), "ConfigureShard runs once per shard");

        plain.Send(new ProbeAddress(nodeA.Id, 5), new Probe(1));
        plain.Send(new ProbeGroup(9), new Probe(2));
        nodeA.Send(new ProbeAddress(nodeA.Id, 6), new Probe(3)); // local: no link

        await Eventually(() => a.Received.Count == 3 && b.Received.Count == 1, "a message did not arrive");
        Assert.That(a.Received.Order(), Is.EqualTo(new[] { "group 9: 2", "node 5: 1", "node 6: 3" }));
        Assert.That(b.Received, Is.EqualTo(new[] { "group 9: 2" }), "a group message reaches each node handling the scope once");

        await nodeB.KillAsync();
        await Eventually(() => a.Dead.Contains(nodeB.Id), "the feature was not told about the dead member");
        Assert.That(b.Stopped, Is.EqualTo("crashed"));
        await Stop(nodeA);
        Assert.That(a.Stopped, Is.EqualTo("stopped"));
    }

    [Test]
    public async Task AnAddressTypeHasOneHandlerPerNode()
    {
        var feature = new ProbeFeature();
        var duplicate = new ProbeFeature();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Start("dup", configure: o => o.Features.AddRange([feature, duplicate])));
    }

    private readonly record struct ProbeAddress(ulong Node, long Id) : INodeAddress;

    private readonly record struct ProbeGroup(long Id) : IGroupAddress;

    private sealed class ProbeFeature : NodeFeature, IAddressHandler
    {
        public readonly ConcurrentQueue<string> Received = new();
        public readonly ConcurrentBag<ulong> Dead = new();
        public int ShardsConfigured;
        public string? Stopped;

        protected internal override void Start(INodeContext node)
        {
            node.HandleAddress<ProbeAddress>(this);
            node.HandleAddress<ProbeGroup>(this);
        }

        protected internal override void ConfigureShard(EasyEcs.World world) => Interlocked.Increment(ref ShardsConfigured);

        protected internal override void OnMemberDead(ClusterMember member) => Dead.Add(member.NodeId);

        protected internal override void Stop(bool crashed) => Stopped = crashed ? "crashed" : "stopped";

        public void OnMessage(in FrameHeader header, ReadOnlySpan<byte> frame, IReplySink? replyTo)
        {
            var reader = new Nino.Core.Reader(FrameHeader.PayloadOf(in header, frame));
            Nino.Core.NinoDeserializer.Deserialize(out Probe probe, ref reader);
            string kind = header.Target == TypeIdOf<ProbeGroup>() ? "group" : "node";
            Received.Enqueue($"{kind} {header.Key}: {probe.N}");
        }

        private static uint TypeIdOf<T>() => WireIds.Fnv1a(typeof(T).FullName!);
    }
}
