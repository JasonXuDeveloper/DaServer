using System.Diagnostics;

namespace DaServer.Tests;

/// <summary>Call cycles fail at once with <see cref="RpcStatus.CallCycle"/> instead of waiting for the timeout (ADR-059).</summary>
public class CallCycleTests : ClusterTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestCase(false, TestName = "AnActorAskedBackByTheActorItWaitsForFailsAtOnce")]
    [TestCase(true, TestName = "ACycleAcrossNodesFailsAtOnce")]
    public async Task ACycleFailsAtOnce(bool acrossNodes)
    {
        var nodes = acrossNodes ? await StartCluster(2) : [await Start("single")];
        long a = KeyOwnedBy<LoopId>(nodes[0]), b = KeyOwnedBy<LoopId>(nodes[^1], a + 1);
        await AssertCycle(nodes[0], new Hop([a, b, a, b], 0, ViaService: false));
    }

    [Test]
    public async Task AnActorAskingItselfFailsAtOnce()
    {
        var node = await Start("single");
        await AssertCycle(node, new Hop([1, 1], 0, ViaService: false));
    }

    [Test]
    public async Task ACycleThroughAServiceFailsAtOnce()
    {
        var node = await Start("single");
        await AssertCycle(node, new Hop([1, 2, 1], 0, ViaService: true));
    }

    [Test]
    public async Task ChainsWithoutACycleAndOtherCallersStillWait()
    {
        var node = await Start("single");
        Assert.That(await node.Ask(new LoopId(1), new Hop([1, 2, 3, 4], 0, ViaService: true)), Is.EqualTo(3), "a chain visiting distinct actors");

        // Another caller's request to an actor whose turn waits queues behind the turn as before.
        var held = node.Ask(new LoopId(5), new Hold(200));
        var behind = node.Ask(new LoopId(1), new Hop([1, 5], 0, ViaService: false));
        Assert.That(await held, Is.EqualTo(200));
        Assert.That(await behind, Is.EqualTo(1));
    }

    private static async Task AssertCycle(Node node, Hop hop)
    {
        var watch = Stopwatch.StartNew();
        var error = Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new LoopId(hop.Path[0]), hop))!;
        Assert.That(watch.Elapsed, Is.LessThan(Timeout / 5), "failed at once, not at the call timeout");
        // The actor asked back fails with CallCycle; the actors waiting for it fail with that status in turn.
        Assert.That(error.Status, Is.EqualTo(RpcStatus.CallCycle), error.Message);
    }
}
