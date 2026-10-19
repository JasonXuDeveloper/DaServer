using DaServer.Testing;

namespace DaServer.Tests;

/// <summary>DaServer.Testing: the in-process cluster users test their actors and systems with, on a manual clock.</summary>
public class TestClusterTests
{
    [Test]
    public async Task AManualClockRunsExactlyTheTicksTheTestSteps()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.ManualTime = true);
        var counter = new CounterId(7_001);
        Assert.That(await cluster.Cluster.Ask(counter, new Grow()), Is.True);

        await Task.Delay(200); // real time passes: no tick
        Assert.That(await cluster.Cluster.Ask(counter, new Get()), Is.Zero);

        await cluster.TickAsync(10);
        Assert.That(await cluster.Cluster.Ask(counter, new Get()), Is.EqualTo(10), "GrowthSystem ran once per tick");
        await cluster.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.That(await cluster.Cluster.Ask(counter, new Get()), Is.EqualTo(30), "one second at 20 ticks a second");
    }

    [Test]
    public async Task DelayedSendsFireWhenTheManualClockReachesThem()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.ManualTime = true);
        CounterId sender = new(7_101), target = new(7_102);
        await cluster.Cluster.Ask(sender, new BumpLater(target, 1000, 5));

        await Task.Delay(300);
        await cluster.AdvanceAsync(TimeSpan.FromMilliseconds(900));
        Assert.That(await cluster.Cluster.Ask(target, new Get()), Is.Zero, "not due yet on the shard clock");

        await cluster.AdvanceAsync(TimeSpan.FromMilliseconds(200));
        Assert.That(await EventuallyEquals(cluster, target, 5), Is.True, "due: sent once the clock passed it");
        Assert.That(cluster.Time.GetUtcNow(), Is.EqualTo(new DateTimeOffset(2000, 1, 1, 0, 0, 1, 100, TimeSpan.Zero)),
            "the clock starts at a fixed instant and moved by what the test stepped");
    }

    [Test]
    public async Task IdleActorsUnloadOnTheManualClock()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.ManualTime = true);
        var visitor = new VisitorId(7_201);
        Assert.That(await cluster.Cluster.Ask(visitor, new Visit()), Is.EqualTo(1));

        await Task.Delay(1500); // more than its UnloadAfterIdle (1 s) in real time
        Assert.That(Observed.Events, Does.Not.Contain("unload:7201"));

        await cluster.AdvanceAsync(TimeSpan.FromMilliseconds(1500));
        await cluster.WaitUntilAsync(() => Observed.Events.Contains("unload:7201"), TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task NodesOfARealTimeClusterServeEachOther()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.Nodes = 3);
        for (int key = 1; key <= 20; key++) await cluster.Node(key % 3).Ask(new CounterId(7_300 + key), new Add(key));
        for (int key = 1; key <= 20; key++)
            Assert.That(await cluster.Node((key + 1) % 3).Ask(new CounterId(7_300 + key), new Get()), Is.EqualTo(key));
        Assert.That(() => cluster.TickAsync(), Throws.InvalidOperationException, "ticks are stepped on a manual clock only");
    }

    [Test]
    public async Task AManualClockRefusesTimers()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.ManualTime = true);
        Assert.That(() => cluster.Time.CreateTimer(static _ => { }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan),
            Throws.TypeOf<NotSupportedException>(), "shard code delays with SendAfter, which follows the clock");
    }

    private static async Task<bool> EventuallyEquals(TestCluster cluster, CounterId id, int value)
    {
        for (int i = 0; i < 200; i++)
        {
            if (await cluster.Cluster.Ask(id, new Get()) == value) return true;
            await Task.Delay(10);
        }

        return false;
    }
}
