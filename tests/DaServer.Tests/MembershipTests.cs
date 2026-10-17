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
