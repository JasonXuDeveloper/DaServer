using System;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Protocol;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>RPC semantics on one node (local delivery through shard mailboxes).</summary>
public class SingleNodeTests
{
    private TestCluster _cluster = null!;
    private Runtime.Node _node = null!;

    [SetUp]
    public async Task Start()
    {
        _cluster = new TestCluster();
        _node = await _cluster.StartNode(TestCluster.Options("single", shards: 4));
    }

    [TearDown]
    public async Task Stop() => await _cluster.DisposeAsync();

    [Test]
    public async Task ActorCallsReturnValuesAndKeepState()
    {
        Assert.That(await _node.Counter(1).Add(5), Is.EqualTo(5));
        Assert.That(await _node.Counter(1).Add(2), Is.EqualTo(7));
        Assert.That(await _node.Counter(2).Get(), Is.EqualTo(0), "state is per key");
        Assert.That(await _node.Counter(1).Echo(new Point(1, 2)), Is.EqualTo(new Point(2, 2)));
    }

    [Test]
    public async Task OneWayAndVoidCalls()
    {
        _node.Counter(3).Bump();
        _node.Counter(3).Bump();
        await TestCluster.Eventually(() => _node.Counter(3).Get().AsTask().Result == 2, "one-way calls not applied");
        await _node.Counter(3).Reset();
        Assert.That(await _node.Counter(3).Get(), Is.EqualTo(0));
    }

    [Test]
    public async Task CallsToOneActorAreSerializedOnItsShard()
    {
        var tasks = Enumerable.Range(0, 1000).Select(_ => _node.Counter(4).Add(1).AsTask());
        var results = await Task.WhenAll(tasks);
        Assert.That(results.Distinct().Count(), Is.EqualTo(1000), "no lost or duplicated updates");
        Assert.That(await _node.Counter(4).Get(), Is.EqualTo(1000));

        string shard = await _node.Counter(4).WhereAmI();
        Assert.That(shard, Does.Contain("shard"));
        Assert.That(await _node.Counter(4).WhereAmI(), Is.EqualTo(shard), "an actor stays on one shard");
    }

    [Test]
    public async Task ActorToActorCallsAndForeignAwaitsResumeOnTheShard()
    {
        Assert.That(await _node.Counter(10).AddVia(11, 3), Is.EqualTo(3));
        Assert.That(await _node.Counter(11).Get(), Is.EqualTo(3));
        Assert.That(await _node.Counter(12).SlowAdd(4), Is.EqualTo(4));
    }

    [Test]
    public async Task ServicesAndErrors()
    {
        Assert.That(await _node.Echo().Echo("hi"), Is.EqualTo("hi"));
        Assert.That(await _node.Echo().NodeId(), Is.EqualTo(_node.Id));

        var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Counter(5).Fail("boom"));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Error));
        Assert.That(error.Message, Does.Contain("boom"));
        Assert.That(await _node.Counter(5).Add(1), Is.EqualTo(1), "the actor survives a failed call");
    }

    [Test]
    public async Task EcsSystemsTickInEveryShard()
    {
        int before = TickCounterSystem.Ticks;
        await TestCluster.Eventually(() => TickCounterSystem.Ticks >= before + 4 * 5, "systems are not ticking");
    }
}
