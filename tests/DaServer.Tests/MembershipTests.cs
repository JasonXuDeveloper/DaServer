namespace DaServer.Tests;

/// <summary>Failure detection under pauses (C1) and keep-majority downing on a split (C2, ADR-064).</summary>
public class MembershipTests : ClusterTest
{
    [Test]
    public async Task APauseOfThisProcessDeclaresNobodyDead()
    {
        var nodes = await StartCluster(3);
        // No gossip reaches a for a moment (as during its pause), so only a's own clock can make the verdict.
        int Port(Node n) => n.ClusterEndPoint.Port;
        nodes[0].Membership.BlockedPorts = [Port(nodes[1]), Port(nodes[2])];
        nodes[1].Membership.BlockedPorts = nodes[2].Membership.BlockedPorts = [Port(nodes[0])];
        nodes[0].SimulatePause(TimeSpan.FromSeconds(5)); // longer than DeadAfter (1.5 s)
        await Task.Delay(300); // a few rounds
        Assert.That(nodes[0].View.UpMembers, Has.Length.EqualTo(3), "the silence was this node's pause, not the others'");
        foreach (var node in nodes) node.Membership.BlockedPorts = null;
    }

    [Test]
    public async Task ANodeLateOnEveryTickStillDeclaresTheDeadDead()
    {
        // a and b are starved for good: each maintenance tick runs over a second late. c goes silent. One pause is
        // excused, but a process late on every tick must still age the others, or c would keep its keys forever.
        var nodes = await StartCluster(3);
        int Port(Node n) => n.ClusterEndPoint.Port;
        nodes[2].Membership.BlockedPorts = [Port(nodes[0]), Port(nodes[1])];
        nodes[0].Membership.BlockedPorts = nodes[1].Membership.BlockedPorts = [Port(nodes[2])];
        var late = Task.Run(async () =>
        {
            while (nodes[0].View.UpMembers.Length == 3)
            {
                nodes[0].SimulatePause(TimeSpan.FromSeconds(1.2), aged: false); // the tick sees a pause the clocks did not
                nodes[1].SimulatePause(TimeSpan.FromSeconds(1.2), aged: false);
                await Task.Delay(100);
            }
        });
        await Eventually(() => nodes[0].View.TryGetMember(nodes[2].Id, out var c) && c.Status == MemberStatus.Dead, "c declared dead", 15_000);
        await late;
        foreach (var node in nodes) node.Membership.BlockedPorts = null;
    }

    [Test]
    public async Task AStalePulseDoesNotLowerAHeartbeat()
    {
        // A pulse saying "suspect" at an older heartbeat supersedes "up" (a worse status), but the heartbeat stays the
        // newest seen: a lower one would make every later stale pulse look like progress of a member that may be dead.
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        int Port(Node node) => node.ClusterEndPoint.Port;
        a.Membership.BlockedPorts = [Port(b)];
        b.Membership.BlockedPorts = [Port(a)];
        Assert.That(a.View.TryGetMember(b.Id, out var seen), Is.True);
        long heartbeat = seen.Heartbeat;
        Span<byte> pulse = stackalloc byte[MemberPulse.Size];
        new MemberPulse(b.Id, seen.Incarnation, heartbeat - 5, MemberStatus.Suspect, 0, 0, 0, 0).Write(pulse);
        a.Membership.MergePulses(pulse);
        Assert.That(a.View.TryGetMember(b.Id, out var suspect) && suspect.Status == MemberStatus.Suspect, Is.True);
        Assert.That(suspect.Heartbeat, Is.GreaterThanOrEqualTo(heartbeat), "the newest heartbeat is kept");
        foreach (var node in new[] { a, b }) node.Membership.BlockedPorts = null;
    }

    [Test]
    public async Task ANodeThatLeftIsForgottenEverywhere()
    {
        // b leaves gracefully: it is never declared dead, only forgotten. a must not keep the link b introduced itself
        // on (one entry per node that ever joined, for good).
        var a = await Start("a");
        var b = await Start("b");
        await Converged();
        await Stop(b);
        await Eventually(() => !a.View.TryGetMember(b.Id, out _), "a forgot b", 20_000);
        Assert.That(a.Introduced(b.Id), Is.False);
    }

    [Test]
    public async Task TheMinoritySideOfASplitDownsItselfAndTheMajorityKeepsRunning()
    {
        var nodes = await StartCluster(3);
        var downed = nodes.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (int i = 0; i < nodes.Length; i++)
        {
            int index = i;
            nodes[i].Downed += () => downed[index].TrySetResult();
        }

        // {a} | {b, c}: gossip between the sides is dropped.
        int Port(Node n) => n.ClusterEndPoint.Port;
        nodes[0].Membership.BlockedPorts = [Port(nodes[1]), Port(nodes[2])];
        nodes[1].Membership.BlockedPorts = [Port(nodes[0])];
        nodes[2].Membership.BlockedPorts = [Port(nodes[0])];

        await downed[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Eventually(() => nodes[1].View.UpMembers.Length == 2 && nodes[2].View.UpMembers.Length == 2, "the majority saw the minority die");
        await Task.Delay(500);
        Assert.That(downed[1].Task.IsCompleted || downed[2].Task.IsCompleted, Is.False, "the majority side keeps running");
        Assert.That(nodes[0].IsReady, Is.False);
        Assert.That(await nodes[1].Ask(new CounterId(1), new Add(1)), Is.EqualTo(1), "the majority serves every key");
    }

    [Test]
    public async Task ASurvivorOfATwoNodeClusterKeepsRunning()
    {
        var nodes = await StartCluster(2);
        bool downed = false;
        foreach (var node in nodes) node.Downed += () => downed = true;
        await nodes[1].KillAsync(); // two members: a crash and a split look the same, nobody downs itself
        await Eventually(() => !nodes[0].View.TryGetMember(nodes[1].Id, out var b) || b.Status == MemberStatus.Dead,
            "the crash was detected (a suspect member still owns its keys)");
        await Task.Delay(500);
        Assert.That(downed, Is.False);
        Assert.That(await nodes[0].Ask(new CounterId(2), new Add(1)), Is.EqualTo(1));
    }

    [Test]
    public async Task AForgottenMemberDoesNotComeBack()
    {
        // x died and was forgotten here; a node that forgets it later still gossips about it: neither its Dead nor an
        // older Up card may bring it back (it would stay for good and count as lost for keep-majority).
        var a = await Start("a");
        static byte[] Cards(MemberStatus status) => Nino.Core.NinoSerializer.Serialize(new[]
        {
            new ClusterMember { NodeId = 4242, Name = "x", Host = "127.0.0.1", Port = 1, Status = status, Incarnation = 1, UpSince = 1 },
        });
        a.Membership.OnGossip(null!, ControlOp.Cards, Cards(MemberStatus.Up));
        Assert.That(a.View.TryGetMember(4242, out _), Is.True);

        long dead = (long)a.Options.DeadAfter.TotalMilliseconds;
        a.Membership.Age(2 * dead);
        a.Membership.RoundNow(); // dead
        a.Membership.Age(4 * dead);
        a.Membership.RoundNow(); // forgotten
        Assert.That(a.View.TryGetMember(4242, out _), Is.False, "forgotten");

        a.Membership.OnGossip(null!, ControlOp.Cards, Cards(MemberStatus.Dead));
        a.Membership.OnGossip(null!, ControlOp.Cards, Cards(MemberStatus.Up));
        Assert.That(a.View.TryGetMember(4242, out _), Is.False, "not brought back by later gossip");
    }

    /// <summary>Two nodes on one store; a key a owns, deposited 5 on a (written only when a unloads it gracefully).</summary>
    private async Task<(Node A, Node B, BankId Key)> Deposited()
    {
        var store = new DaServer.Persistence.InMemoryActorStore();
        var a = await Start("a", configure: o => o.ActorStore = store);
        var b = await Start("b", configure: o => o.ActorStore = store);
        await Converged();
        long key = 100;
        while (a.OwnerOf<BankId>(key) != a.Id) key++;
        Assert.That(await a.Ask(new BankId(key), new Deposit(5, false)), Is.EqualTo(5));
        return (a, b, new BankId(key));
    }

    [Test]
    public async Task APauseLongerThanDeadAfterFencesThisNode()
    {
        // The others declared a dead during its pause and own its keys now: a's actors go without a write, and a comes
        // back at a new incarnation (no Dead pulse may ever reach it: it may be forgotten already).
        var (a, _, key) = await Deposited();
        int incarnation = a.Membership.Self.Incarnation;
        a.Membership.Tick(DaServer.Concurrency.Platform.Milliseconds, pausedMs: 3 * (long)a.Options.DeadAfter.TotalMilliseconds);
        await Eventually(() => a.ActorCount == 0, "a's actors went");
        Assert.That(a.Membership.Self.Incarnation, Is.GreaterThan(incarnation));
        var c = await Start("c", configure: o => o.ActorStore = a.Options.ActorStore);
        Assert.That(await c.Ask(key, new Balance()), Is.Zero, "nothing written: the new owner's state is not overwritten");
    }

}

/// <summary>Node authentication (C3, ADR-065): only nodes of the same cluster holding its secret join.</summary>
public class LinkAuthTests : ClusterTest
{
    private static Action<NodeOptions> With(string? secret, string cluster = "daserver") => o =>
    {
        o.SigningSecret = secret;
        o.ClusterName = cluster;
    };

    [Test]
    public async Task NodesWithTheSecretJoinOthersAreRefused()
    {
        var a = await Start("a", configure: With("s3cret"));
        var b = await Start("b", configure: With("s3cret"));
        await Converged();

        var intruder = await Start("intruder", configure: With("guess"));
        var stranger = await Start("stranger", configure: With("s3cret", cluster: "other"));
        await Task.Delay(1500); // several gossip rounds against the seed
        Assert.That(a.View.UpMembers.Select(static m => m.Name), Is.EquivalentTo(new[] { "a", "b" }),
            "a wrong secret and another cluster's name cannot join");
        Assert.That(intruder.View.UpMembers, Has.Length.EqualTo(1));
        Assert.That(stranger.View.UpMembers, Has.Length.EqualTo(1));
    }

    [Test]
    public void AHelloIsAcceptedOnceAndOnlyWithinItsMinute()
    {
        var auth = new LinkAuth("c", "s3cret");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var proof = auth.Prove(7, 1, now);
        Assert.That(auth.Verify(7, 1, now, proof), Is.Null);
        Assert.That(auth.Verify(7, 1, now, proof), Does.Contain("replayed"));
        Assert.That(auth.Verify(7, 2, now, proof), Does.Contain("secret"), "the proof covers the stream id");
        long old = now - 120_000;
        Assert.That(auth.Verify(7, 1, old, auth.Prove(7, 1, old)), Does.Contain("minute"));
        Assert.That(new LinkAuth("other", "s3cret").Verify(7, 1, now, auth.Prove(7, 1, now)), Does.Contain("secret"),
            "the proof covers the cluster name");
    }
}
