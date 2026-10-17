namespace DaServer.Tests;

/// <summary>Multi-node behaviour over real loopback TCP: discovery, routing, single loaded actor, failover.</summary>
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
            foreach (var node in nodes) await node.Ask(new CounterId(key), new Add(1));
            Assert.That(await nodes[0].Ask(new CounterId(key), new Get()), Is.EqualTo(3), $"key {key} has more than one loaded actor");
        }

        var owners = Enumerable.Range(1, 30).Select(k => nodes[0].OwnerOf<CounterId>(k));
        Assert.That(owners.Distinct().Count(), Is.EqualTo(3), "keys spread over the cluster");
    }

    [Test]
    public async Task ActorsCallActorsOnOtherNodes()
    {
        var (a, b) = await StartPair();
        long onA = KeyOwnedBy<CounterId>(a), onB = KeyOwnedBy<CounterId>(b);
        Assert.That(await a.Ask(new CounterId(onA), new AddVia(new CounterId(onB), 5)), Is.EqualTo(5));
        Assert.That(await b.Ask(new CounterId(onB), new Get()), Is.EqualTo(5));
    }

    [Test]
    public async Task ServicesPreferTheLocalNode()
    {
        var (a, b) = await StartPair();
        Assert.That(await a.Ask(new WhichNode()), Is.EqualTo(a.Id));
        Assert.That(await b.Ask(new WhichNode()), Is.EqualTo(b.Id));
    }

    [Test]
    public async Task MembersAdvertiseTheContractsTheyHost()
    {
        var (a, _) = await StartPair();
        Assert.That(a.View.UpMembers.All(m => m.Handlers.Any(h => h.HandlerTypeId == ActorIds<CounterId>.HandlerTypeId)));
    }

    [Test]
    public async Task KeysMoveToSurvivorsWhenANodeLeaves()
    {
        var (a, b) = await StartPair();
        long key = KeyOwnedBy<CounterId>(b);
        Assert.That(await a.Ask(new CounterId(key), new Add(7)), Is.EqualTo(7));

        await Stop(b); // graceful: Leaving is gossiped, b unloads its actors
        await Eventually(() => a.View.UpMembers.Length == 1, "a still sees b as Up");

        // Reactivated on a; in-memory state is gone (persist it in OnUnload / load in OnLoad).
        Assert.That(await a.Ask(new CounterId(key), new Add(1)), Is.EqualTo(1));
        Assert.That(a.OwnerOf<CounterId>(key), Is.EqualTo(a.Id));
    }

    [Test]
    public async Task KeysMoveToSurvivorsWhenANodeCrashes()
    {
        var (a, b) = await StartPair();
        long key = KeyOwnedBy<CounterId>(b);
        Assert.That(await a.Ask(new CounterId(key), new Add(3)), Is.EqualTo(3));

        await b.KillAsync(); // suspect first (keys stay), then dead (keys move)
        await Eventually(() => a.OwnerOf<CounterId>(key) == a.Id, "the key never moved");
        Assert.That(await a.Ask(new CounterId(key), new Add(1)), Is.EqualTo(1), "reactivated on the survivor");
    }

    [Test]
    public async Task CallsInFlightToACrashedNodeFailWhenItIsDeclaredDead()
    {
        var (a, b) = await StartPair();
        long key = KeyOwnedBy<CounterId>(b);
        Assert.That(await a.Ask(new CounterId(key), new Add(1)), Is.EqualTo(1));

        // Sent after the crash, while b is only suspect: the key stays on b and the calls wait in the link's stream.
        await b.KillAsync();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var calls = new Reply<int>[100];
        for (int i = 0; i < calls.Length; i++) calls[i] = a.Ask(new CounterId(key), new Add(1));

        foreach (var call in calls)
        {
            var error = Assert.ThrowsAsync<RpcException>(async () => await call);
            Assert.That(error!.Status, Is.Not.EqualTo(RpcStatus.Ok));
        }

        // Failed once the cluster declared b dead (DeadAfter 1.5 s), not at the 5 s call timeout.
        Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
        Assert.That(await a.Ask(new CounterId(key), new Add(1)), Is.EqualTo(1), "the key moved to the survivor");
    }

    [Test]
    public async Task RequestsNobodyHandlesFailFast()
    {
        var a = await Start("a");
        var error = Assert.ThrowsAsync<RpcException>(async () => await a.Ask(new Unhandled()));
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

    [Test]
    public async Task ANodeRestartedAtTheSameAddressLosesNoCallWhenItsOldIdIsDeclaredDead()
    {
        // b crashes and comes back at its address with a new id: links are per address, so b's old id must not take the
        // new process's stream with it, neither when the link reconnects nor when the old id is declared dead.
        var a = await Start("a");
        var b = await Start("b");
        await Converged();
        int port = b.ClusterEndPoint.Port;
        await b.KillAsync();
        var restarted = await Start("b2", configure: o => o.ClusterPort = port);
        await Eventually(() => a.View.TryGetMember(restarted.Id, out var m) && m.Status == MemberStatus.Up, "a sees the new process");
        var keys = new List<long>();
        for (long key = 1; keys.Count < 10; key++)
            if (a.OwnerOf<CounterId>(key) == restarted.Id) keys.Add(key);

        var failures = new List<string>();
        var start = DateTime.UtcNow;
        var until = DateTime.UtcNow.AddSeconds(3); // across b's Dead verdict (DeadAfter 1.5 s)
        while (DateTime.UtcNow < until)
            foreach (long key in keys)
            {
                try
                {
                    await a.Ask(new CounterId(key), new Add(1));
                }
                catch (RpcException e)
                {
                    failures.Add($"{e.Status} (key {key} at {(DateTime.UtcNow - start).TotalMilliseconds:0} ms)");
                }
            }

        Assert.That(a.View.TryGetMember(b.Id, out var old) ? old.Status : MemberStatus.Dead, Is.EqualTo(MemberStatus.Dead), "b's old id was declared dead meanwhile");
        Assert.That(failures, Is.Empty);
    }
}
