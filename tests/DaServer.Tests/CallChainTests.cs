using System.Diagnostics;

namespace DaServer.Tests;

/// <summary>
/// Handlers never wait (ADR-081): an ask's answer is mail, so a chain of asks that comes back to an actor whose own ask
/// is still out (A asks B asks A) completes, on one node or across nodes, directly or through a service.
/// </summary>
public class CallChainTests : ClusterTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestCase(false, TestName = "AnActorAskedBackByTheActorItAskedAnswers")]
    [TestCase(true, TestName = "AChainBackAndForthAcrossNodesCompletes")]
    public async Task AChainBackToAnAskingActorCompletes(bool acrossNodes)
    {
        var nodes = acrossNodes ? await StartCluster(2) : [await Start("single")];
        long a = KeyOwnedBy<LoopId>(nodes[0]), b = KeyOwnedBy<LoopId>(nodes[^1], a + 1);
        await AssertCompletes(nodes[0], new Hop([a, b, a, b], 0, ViaService: false));
    }

    [Test]
    public async Task AnActorAskingItselfAnswers()
    {
        var node = await Start("single");
        await AssertCompletes(node, new Hop([1, 1], 0, ViaService: false));
    }

    [Test]
    public async Task AChainThroughAServiceBackToAnAskingActorCompletes()
    {
        var node = await Start("single");
        await AssertCompletes(node, new Hop([1, 2, 1], 0, ViaService: true));
    }

    [Test]
    public async Task ACreatedHandlerAskingTheActorWhoseRequestLoadsItIsAnswered()
    {
        // 1,000 asks 2,000, whose Created asks 1,000 back while 1,000's own ask waits for 2,000: both are answered.
        var node = await Start("single");
        await AssertCompletes(node, new Hop([1_000, 2_000], 0, ViaService: false));
        await Eventually(() => Observed.Events.Contains($"asked-back:2000:{RpcStatus.Ok}"), "the ask from Created was answered");
        Assert.That(await node.Ask(new LoopId(1_000), new Hold(0)), Is.Zero, "1,000 serves on");
    }

    [Test]
    public async Task ChainsVisitingDistinctActorsAndOtherCallersComplete()
    {
        var node = await Start("single");
        Assert.That(await node.Ask(new LoopId(1), new Hop([1, 2, 3, 4], 0, ViaService: true)), Is.EqualTo(3), "a chain visiting distinct actors");

        // An actor that keeps a reply for later takes another caller's request meanwhile: nothing queues behind it.
        var held = node.Ask(new LoopId(5), new Hold(500)).AsTask();
        Assert.That(await node.Ask(new LoopId(1), new Hop([1, 5], 0, ViaService: false)), Is.EqualTo(1));
        Assert.That(held.IsCompleted, Is.False, "answered before the kept reply");
        Assert.That(await held, Is.EqualTo(500));
    }

    private static async Task AssertCompletes(Node node, Hop hop)
    {
        var watch = Stopwatch.StartNew();
        Assert.That(await node.Ask(new LoopId(hop.Path[0]), hop), Is.EqualTo(hop.Path.Length - 1), "the last actor of the path answered");
        Assert.That(watch.Elapsed, Is.LessThan(Timeout / 5), "at once, not at the call timeout");
    }
}
