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
        nodes[0].Membership.SimulatePause(TimeSpan.FromSeconds(5)); // longer than DeadAfter (1.5 s)
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
        await Eventually(() => nodes[0].View.UpMembers.Length == 1, "the crash was detected");
        await Task.Delay(500);
        Assert.That(downed, Is.False);
        Assert.That(await nodes[0].Ask(new CounterId(2), new Add(1)), Is.EqualTo(1));
    }
}
