using DaServer;
using DaServer.Runtime;
using FlashSale.Components;
using FlashSale.Contracts;

namespace FlashSale.Server;

/// <summary>
/// The sale as clients see it: their messages turned into asks of the sale's actors. Stateless (what it remembers is
/// its shard's <see cref="KnownSales"/>): every node's gate serves every buyer, so the gates scale with the nodes.
/// </summary>
public sealed class Gate : Service
{
    [Handle]
    public async Reply<bool> On(OpenSale m)
    {
        await Cluster.Ask(new SaleId(m.Sale), new Open(m.PerSecond)); // first: the shards report to an open sale
        for (int shard = 0; shard < StockShardId.Shards; shard++)
            await Cluster.Ask(StockShardId.Of(m.Sale, shard),
                new Restock(m.Units / StockShardId.Shards + (shard < m.Units % StockShardId.Shards ? 1 : 0)));
        return true;
    }

    [Handle]
    public async Reply<OrderStatus> On(PlaceOrder m)
    {
        var sale = await Info(m.Sale);
        if (sale.SoldOut) return Array.BinarySearch(sale.Buyers, m.User) >= 0 ? OrderStatus.Confirmed : OrderStatus.SoldOut;
        // A queued sale sells to admitted positions only: the sale says whether this one was.
        if (sale.Queued && await Cluster.Ask(new SaleId(m.Sale), new Admitted(m.Position)) != Admission.Admitted) return OrderStatus.None;
        var status = await Cluster.Ask(OrderId.Of(m.Sale, m.User), new Buy());
        if (status == OrderStatus.SoldOut) Forget(m.Sale); // every shard was empty: ask the sale again, it knows the buyers soon
        return status;
    }

    [Handle]
    public async Reply<long> On(QueueUp m)
    {
        if ((await Info(m.Sale)).SoldOut) return 0;
        long position = await Cluster.Ask(new SaleId(m.Sale), new Join());
        if (position == 0) Forget(m.Sale);
        return position;
    }

    [Handle]
    public async Reply<Admission> On(CheckIn m)
    {
        var admission = await Cluster.Ask(new SaleId(m.Sale), new Admitted(m.Position));
        if (admission == Admission.SoldOut) Forget(m.Sale);
        return admission;
    }

    [Handle]
    public async Reply<int> On(SaleStock m)
    {
        int left = 0;
        for (int shard = 0; shard < StockShardId.Shards; shard++) left += await Cluster.Ask(StockShardId.Of(m.Sale, shard), new UnitsLeft());
        return left;
    }

    [Handle] public Reply<OrderStatus> On(OrderOf m) => Cluster.Ask(OrderId.Of(m.Sale, m.User), new GetOrder());

    /// <summary>What this shard knows of the sale, asked of the sale once (and again after <see cref="Forget"/>).</summary>
    private async Reply<SaleInfo> Info(long sale) // a helper a handler awaits returns Reply: pooled, nothing allocated
    {
        if (Known.TryGetValue(sale, out var info)) return info;
        info = await Cluster.Ask(new SaleId(sale), new Describe());
        Known[sale] = info;
        return info;
    }

    private void Forget(long sale) => Known.Remove(sale);

    private Dictionary<long, SaleInfo> Known => World.HasSingleton<KnownSales>()
        ? World.GetSingleton<KnownSales>().Sales
        : (World.AddSingleton<KnownSales>() = new KnownSales(new Dictionary<long, SaleInfo>())).Sales;
}
