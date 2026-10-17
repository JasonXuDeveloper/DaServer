using DaServer.Testing;
using FlashSale.Contracts;
using NUnit.Framework;

namespace FlashSale.Tests;

/// <summary>The sale's guarantees, in an in-process cluster: no unit sold twice, retries answered alike, admission paced.</summary>
public class SaleTests
{
    [Test]
    public async Task EveryUnitIsSoldOnceAndRetriesGetTheSameAnswer()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.Nodes = 2);
        const long sale = 1;
        const int units = 100, buyers = 400;
        for (int shard = 0; shard < Sale.Shards; shard++)
            await cluster.Cluster.Ask(StockShardId.Of(sale, shard), new Restock(units / Sale.Shards + (shard < units % Sale.Shards ? 1 : 0)));

        var orders = new Task<OrderStatus>[buyers];
        for (int user = 0; user < buyers; user++) // all at once, through both nodes
            orders[user] = cluster.Node(user % 2).Ask(OrderId.Of(sale, user), new Buy()).AsTask();
        var outcomes = await Task.WhenAll(orders);
        Assert.That(outcomes.Count(static s => s == OrderStatus.Confirmed), Is.EqualTo(units), "every unit sold, none twice");
        Assert.That(outcomes.Count(static s => s == OrderStatus.SoldOut), Is.EqualTo(buyers - units));

        for (int user = 0; user < buyers; user += 7)
            Assert.That(await cluster.Node(1 - user % 2).Ask(OrderId.Of(sale, user), new Buy()), Is.EqualTo(outcomes[user]), "a retry");
        int left = 0;
        for (int shard = 0; shard < Sale.Shards; shard++) left += await cluster.Cluster.Ask(StockShardId.Of(sale, shard), new UnitsLeft());
        Assert.That(left, Is.Zero);
    }

    [Test]
    public async Task TheWaitingRoomAdmitsBuyersAtItsPace()
    {
        await using var cluster = await TestCluster.StartAsync(o => o.ManualTime = true);
        var room = new WaitingRoomId(2);
        await cluster.Cluster.Ask(room, new OpenRoom(PerSecond: 100));
        for (int i = 0; i < 1000; i++) await cluster.Cluster.Ask(room, new Join());

        Assert.That(await cluster.Cluster.Ask(room, new Admitted(1)), Is.EqualTo(Admission.Waiting), "nobody before the clock moves");
        await cluster.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.That(await cluster.Cluster.Ask(room, new Admitted(100)), Is.EqualTo(Admission.Admitted), "one second: 100 admitted");
        Assert.That(await cluster.Cluster.Ask(room, new Admitted(101)), Is.EqualTo(Admission.Waiting));
        await cluster.AdvanceAsync(TimeSpan.FromSeconds(9));
        Assert.That(await cluster.Cluster.Ask(room, new Admitted(1000)), Is.EqualTo(Admission.Admitted), "ten seconds: everybody");

        for (int shard = 0; shard < Sale.Shards; shard++) await cluster.Cluster.Ask(StockShardId.Of(2, shard), new Restock(0));
        await cluster.WaitUntilAsync(() => cluster.Cluster.Ask(room, new Join()).AsTask().Result == 0, TimeSpan.FromSeconds(5),
            "every shard empty: the room turns newcomers away");
        Assert.That(await cluster.Cluster.Ask(room, new Admitted(1000)), Is.EqualTo(Admission.Admitted), "admitted stays admitted");
        long late = await cluster.Cluster.Ask(room, new Join()); // 0: turned away
        Assert.That(late, Is.Zero);
    }
}
