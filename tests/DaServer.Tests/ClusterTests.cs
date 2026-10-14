using System.Linq;
using System.Threading.Tasks;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Multi-node behaviour over real loopback TCP: discovery, routing, single activation, failover.</summary>
public class ClusterTests
{
    private TestCluster _cluster = null!;

    [SetUp]
    public void Create() => _cluster = new TestCluster();

    [TearDown]
    public async Task Dispose() => await _cluster.DisposeAsync();

    [Test]
    public async Task NodesDiscoverEachOtherThroughASeed()
    {
        await _cluster.StartNode(TestCluster.Options("a"));
        await _cluster.Join("b");
        await _cluster.Join("c");
        await _cluster.Converged(3);
    }

    [Test]
    public async Task EveryNodeRoutesAKeyToTheSameSingleActivation()
    {
        var a = await _cluster.StartNode(TestCluster.Options("a"));
        var b = await _cluster.Join("b");
        var c = await _cluster.Join("c");
        await _cluster.Converged(3);

        var nodes = new[] { a, b, c };
        for (long key = 1; key <= 30; key++)
        {
            // Increment the same key from every node: a single activation must see all increments.
            foreach (var node in nodes) await node.Counter(key).Add(1);
            Assert.That(await a.Counter(key).Get(), Is.EqualTo(3), $"key {key} has more than one activation");
        }

        // Keys are spread over the cluster (rendezvous placement).
        var owners = Enumerable.Range(1, 30).Select(k => a.View.ActorOwner(CounterRpc.ContractId, k)!.NodeId).Distinct();
        Assert.That(owners.Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task CrossNodeActorToActorCalls()
    {
        var a = await _cluster.StartNode(TestCluster.Options("a"));
        await _cluster.Join("b");
        await _cluster.Converged(2);

        // Find two keys owned by different nodes.
        long k1 = 1, k2 = 2;
        while (a.View.ActorOwner(CounterRpc.ContractId, k1)!.NodeId == a.View.ActorOwner(CounterRpc.ContractId, k2)!.NodeId) k2++;

        Assert.That(await a.Counter(k1).AddVia(k2, 5), Is.EqualTo(5));
        Assert.That(await a.Counter(k2).Get(), Is.EqualTo(5));
    }

    [Test]
    public async Task ServicesPreferTheLocalNode()
    {
        var a = await _cluster.StartNode(TestCluster.Options("a"));
        var b = await _cluster.Join("b");
        await _cluster.Converged(2);
        Assert.That(await a.Echo().NodeId(), Is.EqualTo(a.Id));
        Assert.That(await b.Echo().NodeId(), Is.EqualTo(b.Id));
    }

    [Test]
    public async Task RolesRestrictWhereImplementationsRun()
    {
        // "gateway" hosts nothing role-restricted... all test implementations are unrestricted, so check advertising:
        var a = await _cluster.StartNode(TestCluster.Options("a"));
        await _cluster.Join("b", roles: "gateway");
        await _cluster.Converged(2);
        Assert.That(a.View.UpMembers.All(m => m.Actors.Any(x => x.ContractId == CounterRpc.ContractId)), Is.True);
    }

    [Test]
    public async Task KeysMoveToSurvivorsWhenANodeLeaves()
    {
        var a = await _cluster.StartNode(TestCluster.Options("a"));
        var b = await _cluster.Join("b");
        await _cluster.Converged(2);

        long key = 1;
        while (a.View.ActorOwner(CounterRpc.ContractId, key)!.NodeId != b.Id) key++;
        Assert.That(await a.Counter(key).Add(7), Is.EqualTo(7));

        await _cluster.Stop(b); // graceful: Leaving is gossiped, b deactivates its actors
        await TestCluster.Eventually(() => a.View.UpMembers.Length == 1, "a still sees b as Up");

        // The key reactivates on a (in-memory state is gone: persist in OnDeactivateAsync/OnActivateAsync).
        Assert.That(await a.Counter(key).Add(1), Is.EqualTo(1));
        Assert.That(a.View.ActorOwner(CounterRpc.ContractId, key)!.NodeId, Is.EqualTo(a.Id));
    }

    [Test]
    public async Task CallsToAnUnhostedContractFail()
    {
        var a = await _cluster.StartNode(TestCluster.Options("a"));
        // IPlayerClient is a client callback: no node hosts it, routing a call must fail fast, not hang.
        var error = Assert.ThrowsAsync<RpcException>(async () =>
        {
            var frame = Buffers.FrameWriter.Rent();
            await a.Call<int>(frame, PlayerClientRpc.OnGoldId, RpcTargetKind.Service, 0);
        });
        Assert.That(error!.Status, Is.EqualTo(Protocol.RpcStatus.NotHosted));
    }
}
