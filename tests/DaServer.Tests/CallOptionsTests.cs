using System.Diagnostics;

namespace DaServer.Tests;

/// <summary>Per-call options (K4): a call's own timeout, and the time its caller still waits as handlers see it.</summary>
public class CallOptionsTests : ClusterTest
{
    [Test]
    public async Task ACallTimesOutAtItsOwnTimeout()
    {
        var node = await Start("single"); // CallTimeout 5 s
        var watch = Stopwatch.StartNew();
        var error = Assert.ThrowsAsync<RpcException>(async () =>
            await node.Ask(new LoopId(1), new Hold(1_000), new CallOptions(TimeSpan.FromMilliseconds(300))))!;
        Assert.That(error.Status, Is.EqualTo(RpcStatus.Timeout));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(0.9)), "the call's timeout, not the node's");
        Assert.That(await node.Ask(new LoopId(1), new Hold(0)), Is.Zero, "the actor serves while the late reply is kept");
    }

    [Test]
    public async Task HandlersReadTheTimeTheirCallerStillWaits()
    {
        var node = await Start("single");
        long service = await node.Ask(new HowLong(), new CallOptions(TimeSpan.FromSeconds(3)));
        Assert.That(service, Is.InRange(2_000, 3_000), "a service, from the call's own timeout");
        Assert.That(await node.Ask(new HowLong()), Is.InRange(4_000, 5_000), "the node's call timeout by default");
        long actor = await node.Ask(new LoopId(10), new HowLong(), new CallOptions(TimeSpan.FromSeconds(3)));
        Assert.That(actor, Is.InRange(2_000, 3_000), "an actor, from the call's own timeout");
    }

    [Test]
    public async Task AClientCallTimesOutAtItsOwnTimeout()
    {
        var gateway = await Start("gw", gateway: true);
        var client = Track(new ClusterClient()); // CallTimeout 10 s
        await client.ConnectAsync("127.0.0.1", gateway.ClientPort());
        var watch = Stopwatch.StartNew();
        var error = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new Stall(), new CallOptions(TimeSpan.FromMilliseconds(300))))!;
        Assert.That(error.Status, Is.EqualTo(RpcStatus.Timeout));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "the call's timeout, not the client's");
        Assert.That(await gateway.Ask(Staller.One, new ReleaseStalls()), Is.EqualTo(1), "the stall was held, its caller gone");
    }
}
