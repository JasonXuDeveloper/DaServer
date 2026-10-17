using DaServer;
using DaServer.Rpc;
using DaServer.Runtime;
using FlashSale.Components;
using FlashSale.Contracts;

namespace FlashSale.Server;

/// <summary>
/// The sale as clients see it: their messages turned into asks of the sale's actors. Stateless: every node's gate
/// serves every buyer, so the gate scales with the nodes and the actors decide.
/// </summary>
public sealed class Gate : Service
{
    [Handle]
    public async Reply<bool> On(OpenSale m)
    {
        await Cluster.Ask(new WaitingRoomId(m.Sale), new OpenRoom(m.PerSecond)); // first: the shards report to an open room
        for (int shard = 0; shard < Sale.Shards; shard++)
            await Cluster.Ask(StockShardId.Of(m.Sale, shard), new Restock(m.Units / Sale.Shards + (shard < m.Units % Sale.Shards ? 1 : 0)));
        return true;
    }

    [Handle]
    public async Reply<long> On(QueueUp m)
    {
        if (SoldOut.Contains(m.Sale)) return 0;
        long position = await Cluster.Ask(new WaitingRoomId(m.Sale), new Join());
        if (position == 0) SoldOut.Add(m.Sale);
        return position;
    }

    [Handle]
    public async Reply<Admission> On(CheckIn m)
    {
        var admission = await Cluster.Ask(new WaitingRoomId(m.Sale), new Admitted(m.Position));
        if (admission == Admission.SoldOut) SoldOut.Add(m.Sale);
        return admission;
    }

    [Handle]
    public async Reply<OrderStatus> On(PlaceOrder m)
    {
        // The room says whether the position was admitted: a client cannot skip the queue by sending a position.
        if (await Cluster.Ask(new WaitingRoomId(m.Sale), new Admitted(m.Position)) != Admission.Admitted) return OrderStatus.None;
        return await Cluster.Ask(OrderId.Of(m.Sale, m.User), new Buy());
    }

    [Handle]
    public async Reply<int> On(SaleStock m)
    {
        int left = 0;
        for (int shard = 0; shard < Sale.Shards; shard++) left += await Cluster.Ask(StockShardId.Of(m.Sale, shard), new UnitsLeft());
        return left;
    }

    [Handle] public Reply<OrderStatus> On(OrderOf m) => Cluster.Ask(OrderId.Of(m.Sale, m.User), new GetOrder());

    /// <summary>The sales this shard knows are sold out (its world's <see cref="SoldOutSales"/>).</summary>
    private HashSet<long> SoldOut => World.HasSingleton<SoldOutSales>()
        ? World.GetSingleton<SoldOutSales>().Sales
        : (World.AddSingleton<SoldOutSales>() = new SoldOutSales(new HashSet<long>())).Sales;
}
