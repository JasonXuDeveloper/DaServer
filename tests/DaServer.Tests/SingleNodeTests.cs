using System;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Protocol;
using DaServer.Rpc;
using DaServer.Runtime;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>RPC semantics on one node (local delivery through shard mailboxes).</summary>
public class SingleNodeTests : ClusterTest
{
    private Node _node = null!;

    [SetUp]
    public async Task StartNode() => _node = await Start("single", shards: 4);

    [Test]
    public async Task ActorsKeepStatePerKey()
    {
        Assert.That(await _node.Counter(1).Add(5), Is.EqualTo(5));
        Assert.That(await _node.Counter(1).Add(2), Is.EqualTo(7));
        Assert.That(await _node.Counter(2).Get(), Is.Zero);
        Assert.That(await _node.Counter(1).Echo(new Point(1, 2)), Is.EqualTo(new Point(2, 2)));
    }

    [Test]
    public async Task IdleActorsUnloadAndLoadAgainOnTheNextMessage()
    {
        const long key = 501;
        Assert.That(await _node.Visitor(key).Visit(), Is.EqualTo(1));

        // Kept loaded while it receives mail more often than its UnloadAfterIdle (300 ms).
        for (int i = 2; i <= 5; i++)
        {
            await Task.Delay(100);
            Assert.That(await _node.Visitor(key).Visit(), Is.EqualTo(i));
        }

        Assert.That(Observed.Events, Has.No.Member($"unload:{key}"));
        await Eventually(() => Observed.Events.Contains($"unload:{key}"), "the idle actor was not unloaded");
        await Eventually(() => _node.ActorCount == 0, "the unloaded actor is still in the directory");

        Assert.That(await _node.Visitor(key).Visit(), Is.EqualTo(1), "loaded again with fresh state");
        Assert.That(Observed.Events.Count(e => e == $"load:{key}"), Is.EqualTo(2));
    }

    [Test]
    public async Task CallsFromOneSenderArriveInOrder()
    {
        var counter = _node.Counter(3);
        counter.Bump();               // one-way calls are queued before the request below
        counter.Bump();
        Assert.That(await counter.Get(), Is.EqualTo(2));
        await counter.Reset();
        Assert.That(await counter.Get(), Is.Zero);
    }

    [Test]
    public async Task ConcurrentCallsToOneActorAreSerialized()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => _node.Counter(4).Add(1).AsTask()));
        Assert.That(results.Order(), Is.EqualTo(Enumerable.Range(1, 1000)), "no lost or duplicated updates");

        string shard = await _node.Counter(4).WhereAmI();
        Assert.That(shard, Does.Contain("shard"));
        Assert.That(await _node.Counter(4).WhereAmI(), Is.EqualTo(shard), "an actor stays on one shard");
    }

    [Test]
    public async Task AwaitsResumeOnTheActorsShard()
    {
        Assert.That(await _node.Counter(10).AddVia(11, 3), Is.EqualTo(3));
        Assert.That(await _node.Counter(11).Get(), Is.EqualTo(3));
        Assert.That(await _node.Counter(12).SlowAdd(4), Is.EqualTo(4));
    }

    [Test]
    public async Task ServicesRunOnTheLocalNode()
    {
        Assert.That(await _node.Echo().Echo("hi"), Is.EqualTo("hi"));
        Assert.That(await _node.Echo().NodeId(), Is.EqualTo(_node.Id));
    }

    [Test]
    public async Task ExceptionsBecomeRpcErrorsAndTheActorSurvives()
    {
        var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Counter(5).Fail("boom"));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Error));
        Assert.That(error.Message, Does.Contain("boom"));
        Assert.That(await _node.Counter(5).Add(1), Is.EqualTo(1));
    }

    [Test]
    public async Task SystemsProcessActorStateEveryTick()
    {
        var counter = _node.Counter(6);
        await counter.Grow();         // the actor writes input; GrowthSystem does the per-tick work
        int first = await counter.Get();
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // 50 ticks/s
        Assert.That(await counter.Get(), Is.GreaterThan(first));
        Assert.That(await _node.Counter(7).Get(), Is.Zero, "only growing counters grow");
    }
}
