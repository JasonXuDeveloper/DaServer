namespace DaServer.Tests;

/// <summary>Cluster events: keyed delivery to actor activations, once per handling service implementation.</summary>
public class EventTests : ClusterTest
{
    [SetUp]
    public void ClearObserved() => Observed.Events.Clear();

    [Test]
    public async Task KeyedEventsReachTheActivationOfTheKeyOnAnyNode()
    {
        var nodes = await StartCluster(3);
        for (long key = 1; key <= 20; key++)
            nodes[(int)(key % 3)].Publish(key, new CounterBumped((int)key));

        for (long key = 1; key <= 20; key++)
        {
            long k = key;
            await Eventually(async () => await nodes[0].Ask(k, new Get()) == k, $"event for key {k} not applied");
        }
    }

    [Test]
    public async Task EventsAndCallsOfOneSenderStayInOrder()
    {
        var node = await Start("solo");
        for (int i = 0; i < 100; i++) node.Publish(5, new CounterBumped(1));
        Assert.That(await node.Ask(5, new Get()), Is.EqualTo(100), "the call queued behind the events");
    }

    [Test]
    public async Task EachHandlingServiceReceivesAnEventOnce()
    {
        var nodes = await StartCluster(3);
        nodes[1].Publish(new Ping(1));
        await Eventually(() => Observed.Events.Count >= 2, "event not handled");
        await Task.Delay(200); // duplicates would arrive about as fast

        var handled = Observed.Events.ToArray();
        Assert.That(handled.Count(e => e.StartsWith("echo:1:")), Is.EqualTo(1), "one EchoService in the cluster");
        Assert.That(handled.Count(e => e == "audit:1"), Is.EqualTo(1), "one AuditService in the cluster");
        Assert.That(handled.Single(e => e.StartsWith("echo:")), Does.EndWith($":{nodes[1].Id:x}"), "local handler preferred");
    }

    [Test]
    public async Task EventsWithoutHandlersAreDropped()
    {
        var node = await Start("solo", hosts: t => t != typeof(EchoService) && t != typeof(AuditService));
        node.Publish(new Ping(2));
        await Task.Delay(100);
        Assert.That(Observed.Events, Is.Empty);
    }

    [Test]
    public async Task EventHandlersMoveWithTheHostingNodes()
    {
        var a = await Start("a", hosts: t => t != typeof(Counter));
        var b = await Start("b");
        await Converged();

        a.Publish(3, new CounterBumped(7)); // a hosts no counters: delivered on b
        await Eventually(async () => await b.Ask(3, new Get()) == 7, "event not routed to the hosting node");
        Assert.That(OwnerOf<Counter>(b, 3), Is.EqualTo(b.Id));
    }
}
