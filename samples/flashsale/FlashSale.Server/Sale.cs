using DaServer;
using DaServer.Runtime;
using EasyEcs;
using FlashSale.Components;
using FlashSale.Contracts;

namespace FlashSale.Server;

/// <summary>
/// A shard of a sale's stock. A turn is the atomic decrement: one buyer at a time per shard, so no unit is sold twice
/// without a lock or a Redis counter; shards let buyers decrement in parallel.
/// </summary>
public sealed class StockShard : Actor<StockShardId, Stock>
{
    [Handle]
    public bool On(Restock m)
    {
        State = new Stock(m.Units, new List<long>());
        Entity.GetOrAdd<SaveNow>();
        if (m.Units == 0) Empty();
        return true;
    }

    [Handle]
    public bool On(TakeOne m)
    {
        if (State.Left == 0) return false;
        State.Left--;
        State.Buyers.Add(m.User);
        if (State.Left == 0) Empty();
        Entity.GetOrAdd<SaveNow>(); // a sold unit is written before the next restart could sell it again
        return true;
    }

    [Handle] public int On(UnitsLeft _) => State.Left;

    /// <summary>Tell the sale: once every shard is empty, it knows all the buyers and the gates turn everyone else away.</summary>
    private void Empty() => Cluster.Send(new SaleId(Id.Sale), new ShardEmpty(Id.Shard, State.Buyers.ToArray()));
}

/// <summary>
/// A buyer's order in a sale. Its turn spans the whole purchase, so a retry arriving meanwhile waits and then gets the
/// same answer: retries are idempotent without request ids.
/// </summary>
public sealed class Order : Actor<OrderId, Purchase>
{
    public override TimeSpan UnloadAfterIdle => TimeSpan.FromMinutes(1); // a sale's buyers come once

    [Handle]
    public async Reply<OrderStatus> On(Buy _)
    {
        if (State.Status != OrderStatus.None) return State.Status;
        var status = OrderStatus.SoldOut;
        for (int i = 0; i < StockShardId.Shards; i++) // the buyer's own shard first, then the next ones as they run out
        {
            var shard = StockShardId.Of(Id.Sale, (int)((Id.User + i) % StockShardId.Shards));
            if (await Cluster.Ask(shard, new TakeOne(Id.User)))
            {
                status = OrderStatus.Confirmed;
                break;
            }
        }

        State.Status = status;
        Entity.GetOrAdd<SaveNow>();
        return status;
    }

    [Handle] public OrderStatus On(GetOrder _) => State.Status;
}

/// <summary>
/// A sale: knows when every stock shard is empty and who bought, which the gates ask once and then answer for. A
/// queued sale also hands out positions and admits them at its pace (<see cref="AdmissionSystem"/>).
/// </summary>
public sealed class Sale : Actor<SaleId, SaleState>
{
    [Handle]
    public bool On(Open m)
    {
        State = new SaleState(m.PerSecond, 0, 0, Time.GetUtcNow().ToUnixTimeMilliseconds(), 0, new List<long>()); // before the shards are stocked
        Entity.GetOrAdd<SaveNow>();
        return true;
    }

    /// <summary>Once per gate shard and sale: the buyers sorted, so a late buyer's answer is a binary search.</summary>
    [Handle]
    public SaleInfo On(Describe _)
    {
        long[] buyers = State.SoldOut ? State.Buyers.ToArray() : [];
        Array.Sort(buyers);
        return new SaleInfo(State.Queued, State.SoldOut, buyers);
    }

    [Handle] public long On(Join _) => State.SoldOut ? 0 : ++State.Issued;

    // A position once admitted stays admitted, sold out or not: its buyer may still ask its order (a retry gets the
    // order's answer); only those still waiting are turned away.
    [Handle]
    public Admission On(Admitted m) =>
        m.Position > 0 && m.Position <= State.Released ? Admission.Admitted : State.SoldOut ? Admission.SoldOut : Admission.Waiting;

    [Handle]
    public void On(ShardEmpty m)
    {
        if ((State.EmptyShards & 1 << m.Shard) != 0) return;
        State.EmptyShards |= 1 << m.Shard;
        State.Buyers.AddRange(m.Buyers);
        if (State.SoldOut) Entity.GetOrAdd<SaveNow>();
    }
}

/// <summary>
/// Admits a queued sale's buyers at its pace, every tick. Capacity nobody waited for is not saved up: a burst of
/// arrivals after a quiet minute is admitted at the pace too.
/// </summary>
public sealed partial class AdmissionSystem : EcsSystem
{
    [ForEach]
    private static void Admit(ref SaleState sale, World world)
    {
        if (!sale.Queued) return;
        long now = world.Time.GetUtcNow().ToUnixTimeMilliseconds();
        if (sale.Released >= sale.Issued)
        {
            sale.AtMs = now;
            return;
        }

        long due = (now - sale.AtMs) * sale.PerSecond / 1000;
        if (due <= 0) return;
        sale.AtMs += due * 1000 / sale.PerSecond;
        sale.Released = Math.Min(sale.Issued, sale.Released + due);
    }
}
