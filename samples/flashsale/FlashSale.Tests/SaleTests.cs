using DaServer.Testing;
using FlashSale.Contracts;
using NUnit.Framework;

namespace FlashSale.Tests;

/// <summary>The sale's guarantees in an in-process cluster: no unit sold twice, retries answered alike, queues paced.</summary>
public class SaleTests
{
    [Test]
    public void EveryMessageHasItsHandler() => RouteCheck.Check(typeof(FlashSale.Server.Gate).Assembly);

    [Test]
    public async Task EveryUnitIsSoldOnceAndRetriesGetTheSameAnswer()
    {
        await using var cluster = await TestCluster.StartAsync(o =>
        {
            o.Nodes = 2;
            o.ClientConnections = true;
        });
        var client = await cluster.ConnectAsync();
        const long sale = 1;
        const int units = 100, buyers = 400;
        Assert.That(await client.Ask(new OpenSale(sale, units, PerSecond: 0)), Is.True);

        var orders = new Task<OrderStatus>[buyers];
        for (int user = 0; user < buyers; user++) orders[user] = client.Ask(new PlaceOrder(sale, user, 0)).AsTask(); // all at once
        var outcomes = await Task.WhenAll(orders);
        Assert.That(outcomes.Count(static s => s == OrderStatus.Confirmed), Is.EqualTo(units), "every unit sold, none twice");
        Assert.That(outcomes.Count(static s => s == OrderStatus.SoldOut), Is.EqualTo(buyers - units));

        for (int user = 0; user < buyers; user++) // retries, once the gates know the sale is sold out
            Assert.That(await client.Ask(new PlaceOrder(sale, user, 0)), Is.EqualTo(outcomes[user]), $"a retry of {user}");
        Assert.That(await client.Ask(new PlaceOrder(sale, buyers, 0)), Is.EqualTo(OrderStatus.SoldOut), "a late buyer");
        Assert.That(await client.Ask(new SaleStock(sale)), Is.Zero);
    }

    [Test]
    public async Task AQueuedSaleAdmitsBuyersAtItsPace()
    {
        await using var cluster = await TestCluster.StartAsync(o =>
        {
            o.ManualTime = true;
            o.ClientConnections = true;
        });
        var client = await cluster.ConnectAsync();
        const long sale = 2;
        await client.Ask(new OpenSale(sale, Units: 1000, PerSecond: 100));
        for (int i = 0; i < 1000; i++) await client.Ask(new QueueUp(sale));

        Assert.That(await client.Ask(new CheckIn(sale, 1)), Is.EqualTo(Admission.Waiting), "nobody before the clock moves");
        Assert.That(await client.Ask(new PlaceOrder(sale, 1, 1)), Is.EqualTo(OrderStatus.None), "no skipping the queue");
        Assert.That(await client.Ask(new PlaceOrder(sale, 2, 0)), Is.EqualTo(OrderStatus.None), "position 0 was never issued");
        Assert.That(await client.Ask(new CheckIn(sale, -1)), Is.EqualTo(Admission.Waiting), "nor a negative one");
        await cluster.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.That(await client.Ask(new CheckIn(sale, 100)), Is.EqualTo(Admission.Admitted), "one second: 100 admitted");
        Assert.That(await client.Ask(new CheckIn(sale, 101)), Is.EqualTo(Admission.Waiting));
        Assert.That(await client.Ask(new PlaceOrder(sale, 1, 1)), Is.EqualTo(OrderStatus.Confirmed));
        await cluster.AdvanceAsync(TimeSpan.FromSeconds(9));
        Assert.That(await client.Ask(new CheckIn(sale, 1000)), Is.EqualTo(Admission.Admitted), "ten seconds: everybody");
    }
}
