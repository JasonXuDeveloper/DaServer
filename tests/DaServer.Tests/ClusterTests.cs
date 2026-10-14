using System.Linq;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Protocol;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Multi-node behaviour over real loopback TCP: discovery, routing, single activation, failover.</summary>
public class ClusterTests : ClusterTest
{
    [Test]
    public async Task NodesDiscoverEachOtherThroughASeed() => await StartCluster(3);

    [Test]
    public async Task EveryNodeRoutesAKeyToTheSameActivation()
    {
        var nodes = await StartCluster(3);
        for (long key = 1; key <= 30; key++)
        {
            foreach (var node in nodes) await node.Counter(key).Add(1);
            Assert.That(await nodes[0].Counter(key).Get(), Is.EqualTo(3), $"key {key} has more than one activation");
        }

        var owners = Enumerable.Range(1, 30).Select(k => nodes[0].View.ActorOwner(CounterRpc.ContractId, k)!.NodeId);
        Assert.That(owners.Distinct().Count(), Is.EqualTo(3), "keys spread over the cluster");
    }

    [Test]
    public async Task ActorsCallActorsOnOtherNodes()
    {
        var (a, b) = await StartPair();
        long onA = KeyOwnedBy(a, CounterRpc.ContractId), onB = KeyOwnedBy(b, CounterRpc.ContractId);
        Assert.That(await a.Counter(onA).AddVia(onB, 5), Is.EqualTo(5));
        Assert.That(await b.Counter(onB).Get(), Is.EqualTo(5));
    }

    [Test]
    public async Task ServicesPreferTheLocalNode()
    {
        var (a, b) = await StartPair();
        Assert.That(await a.Echo().NodeId(), Is.EqualTo(a.Id));
        Assert.That(await b.Echo().NodeId(), Is.EqualTo(b.Id));
    }

    [Test]
    public async Task MembersAdvertiseTheContractsTheyHost()
    {
        var (a, _) = await StartPair();
        Assert.That(a.View.UpMembers.All(m => m.Actors.Any(x => x.ContractId == CounterRpc.ContractId)));
    }

    [Test]
    public async Task KeysMoveToSurvivorsWhenANodeLeaves()
    {
        var (a, b) = await StartPair();
        long key = KeyOwnedBy(b, CounterRpc.ContractId);
        Assert.That(await a.Counter(key).Add(7), Is.EqualTo(7));

        await Stop(b); // graceful: Leaving is gossiped, b deactivates its actors
        await Eventually(() => a.View.UpMembers.Length == 1, "a still sees b as Up");

        // Reactivated on a; in-memory state is gone (persist it in OnUnload / load in OnLoad).
        Assert.That(await a.Counter(key).Add(1), Is.EqualTo(1));
        Assert.That(a.View.ActorOwner(CounterRpc.ContractId, key)!.NodeId, Is.EqualTo(a.Id));
    }

    [Test]
    public async Task KeysMoveToSurvivorsWhenANodeCrashes()
    {
        var (a, b) = await StartPair();
        long key = KeyOwnedBy(b, CounterRpc.ContractId);
        Assert.That(await a.Counter(key).Add(3), Is.EqualTo(3));

        await b.KillAsync(); // suspect first (keys stay), then dead (keys move)
        await Eventually(() => a.View.ActorOwner(CounterRpc.ContractId, key)!.NodeId == a.Id, "the key never moved");
        Assert.That(await a.Counter(key).Add(1), Is.EqualTo(1), "reactivated on the survivor");
    }

    [Test]
    public async Task CallsToUnhostedContractsFailFast()
    {
        var a = await Start("a");
        var error = Assert.ThrowsAsync<RpcException>(async () =>
            await a.Call<int>(FrameWriter.Rent(), PlayerClientRpc.OnGoldId, RpcTargetKind.Service, 0));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.NotHosted));
    }

    private async Task<(Runtime.Node A, Runtime.Node B)> StartPair()
    {
        var nodes = await StartCluster(2);
        return (nodes[0], nodes[1]);
    }

    [Test]
    public async Task APortBelongsToOneNodeAndIsReusableAfterARestart()
    {
        var first = await Start("first");
        int port = first.ClusterEndPoint.Port;
        var second = await Start("second"); // links to first: first's sockets go through TIME_WAIT when it stops
        await Converged();

        Assert.ThrowsAsync<System.Net.Sockets.SocketException>(
            async () => await new DaServer.Runtime.Node(new DaServer.Runtime.NodeOptions { ListenAddress = "127.0.0.1", ClusterPort = port }).StartAsync(),
            "a second process on the same port would silently take half of the connections");

        await Stop(first);
        var restarted = await Start("restarted", configure: o => o.ClusterPort = port);
        Assert.That(restarted.ClusterEndPoint.Port, Is.EqualTo(port));
        Assert.That(second.Id, Is.Not.EqualTo(restarted.Id));
    }
}
