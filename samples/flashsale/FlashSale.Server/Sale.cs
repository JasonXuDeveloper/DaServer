using DaServer;
using DaServer.Protocol;
using DaServer.Runtime;
using EasyEcs;
using FlashSale.Components;
using FlashSale.Contracts;

namespace FlashSale.Server;

/// <summary>
/// A shard of a sale's stock. A handler runs to completion, one message at a time per shard: the decrement is atomic,
/// so no unit is sold twice without a lock or a Redis counter; shards let buyers decrement in parallel.
/// </summary>
[Actor<StockShardId>]
public static class StockShard
{
    [Handle] internal static void On(in Created _, ref Stock stock) => stock = new Stock(0, new List<long>());

    [Handle]
    internal static bool On(in Restock m, ref Stock stock, Entity self, in Context ctx)
    {
        stock = new Stock(m.Units, new List<long>());
        if (m.Units == 0) Empty(in stock, in ctx);
        self.GetOrAdd<SaveNow>(); // last: a structural change, after which the ref to the stock is not used
        return true;
    }

    [Handle]
    internal static bool On(in TakeOne m, ref Stock stock, Entity self, in Context ctx)
    {
        if (stock.Left == 0) return false;
        stock.Left--;
        stock.Buyers.Add(m.User);
        if (stock.Left == 0) Empty(in stock, in ctx);
        self.GetOrAdd<SaveNow>(); // a sold unit is written before the next restart could sell it again
        return true;
    }

    [Handle] internal static int On(in UnitsLeft _, in Stock stock) => stock.Left;

    /// <summary>Tell the sale: once every shard is empty, it knows all the buyers and the gates turn everyone else away.</summary>
    private static void Empty(in Stock stock, in Context ctx)
    {
        var id = ctx.Id<StockShardId>();
        ctx.Send(new SaleId(id.Sale), new ShardEmpty(id.Shard, stock.Buyers.ToArray()));
    }
}

/// <summary>
/// A buyer's order in a sale. A purchase takes a unit from the buyer's own shard, or the next ones as they run out; a
/// retry arriving meanwhile waits for it (its reply is kept with the order's) and gets the same answer, as does every
/// retry after it: idempotent without request ids.
/// </summary>
[Actor<OrderId>(UnloadAfterIdleSeconds = 60)] // a sale's buyers come once
public static class Order
{
    [Handle]
    internal static Answer<OrderStatus> On(in Buy _, in Purchase purchase, Entity self, in Context ctx)
    {
        if (purchase.Status != OrderStatus.None) return purchase.Status;
        var caller = ctx.ReplyLater();
        if (caller.Expected) self.GetOrAdd<DynamicBuffer<ReplyTo>>().Add(in caller); // answered with the purchase
        if (self.Has<Buying>()) return Answer<OrderStatus>.Later; // a retry: the purchase is running
        self.Set(new Buying(0));
        TakeFrom(0, in ctx);
        return Answer<OrderStatus>.Later;
    }

    /// <summary>A shard's answer: a unit (confirmed), or empty (the next shard, until every one was tried: sold out).</summary>
    [Handle]
    internal static void On(in Answered<TakeOne, bool> a, Entity self, in Context ctx)
    {
        ref var buying = ref self.TryGetRef<Buying>(out bool running);
        if (!a.Ok)
        {
            // Not answered (refused, timed out): the purchase stops with nothing decided; a retry starts it again.
            if (running) Settle(self, OrderStatus.None, a.Status, in ctx);
            return;
        }

        if (!a.Value)
        {
            if (!running) return; // nothing was taken for a purchase that is not running
            if (++buying.Tried < StockShardId.Shards)
            {
                TakeFrom(buying.Tried, in ctx);
                return;
            }
        }

        Settle(self, a.Value ? OrderStatus.Confirmed : OrderStatus.SoldOut, RpcStatus.Ok, in ctx);
    }

    [Handle] internal static OrderStatus On(in GetOrder _, in Purchase purchase) => purchase.Status;

    private static void TakeFrom(int tried, in Context ctx)
    {
        var id = ctx.Id<OrderId>();
        ctx.Ask(StockShardId.Of(id.Sale, (int)((id.User + tried) % StockShardId.Shards)), new TakeOne(id.User));
    }

    /// <summary>
    /// The purchase ends: decided (<paramref name="status"/>, written at once) or failed (<paramref name="failed"/>);
    /// every caller waiting for it hears the same.
    /// </summary>
    private static void Settle(Entity self, OrderStatus status, RpcStatus failed, in Context ctx)
    {
        if (failed == RpcStatus.Ok)
        {
            self.Set(new Purchase(status));
            self.GetOrAdd<SaveNow>();
        }

        self.Remove<Buying>();
        if (!self.Has<DynamicBuffer<ReplyTo>>()) return;
        ref var callers = ref self.Get<DynamicBuffer<ReplyTo>>();
        foreach (var caller in callers.AsReadOnlySpan())
        {
            if (failed == RpcStatus.Ok) ctx.Reply(caller, status);
            else ctx.Fail(caller, failed);
        }

        callers.Clear();
    }
}

/// <summary>
/// A sale: knows when every stock shard is empty and who bought, which the gates ask once and then answer for. A
/// queued sale also has a <see cref="WaitingRoom"/>: it hands out positions, and <see cref="AdmissionSystem"/> admits
/// them at its pace.
/// </summary>
[Actor<SaleId>]
public static class Sale
{
    [Handle] internal static void On(in Created _, ref Sold sold) => sold = new Sold(0, new List<long>());

    [Handle]
    internal static bool On(in Open m, ref Sold sold, Entity self, in Context ctx)
    {
        sold = new Sold(0, new List<long>()); // before the shards are stocked: they report to an open sale
        if (m.PerSecond > 0) self.Set(new WaitingRoom(m.PerSecond, 0, 0, ctx.World.Time.GetUtcNow().ToUnixTimeMilliseconds()));
        else self.Remove<WaitingRoom>();
        self.GetOrAdd<SaveNow>();
        return true;
    }

    /// <summary>Once per gate shard and sale: the buyers sorted, so a late buyer's answer is a binary search.</summary>
    [Handle]
    internal static SaleInfo On(in Describe _, in Sold sold, Entity self)
    {
        long[] buyers = sold.SoldOut ? sold.Buyers.ToArray() : [];
        Array.Sort(buyers);
        return new SaleInfo(self.Has<WaitingRoom>(), sold.SoldOut, buyers);
    }

    /// <summary>A place in the line: none when sold out, nor in a sale without a line (its buyers buy directly).</summary>
    [Handle]
    internal static long On(in Join _, in Sold sold, Entity self)
    {
        ref var room = ref self.TryGetRef<WaitingRoom>(out bool queued);
        return queued && !sold.SoldOut ? ++room.Issued : 0;
    }

    // A position once admitted stays admitted, sold out or not: its buyer may still ask its order (a retry gets the
    // order's answer); only those still waiting are turned away.
    [Handle]
    internal static Admission On(in Admitted m, in Sold sold, Entity self) =>
        m.Position > 0 && self.TryGet<WaitingRoom>(out var room) && m.Position <= room.Released ? Admission.Admitted
        : sold.SoldOut ? Admission.SoldOut : Admission.Waiting;

    [Handle]
    internal static void On(in ShardEmpty m, ref Sold sold, Entity self)
    {
        if ((sold.EmptyShards & 1 << m.Shard) != 0) return;
        sold.EmptyShards |= 1 << m.Shard;
        sold.Buyers.AddRange(m.Buyers);
        if (sold.SoldOut) self.GetOrAdd<SaveNow>();
    }
}

/// <summary>
/// Admits a queued sale's buyers at its pace, every tick. Capacity nobody waited for is not saved up: a burst of
/// arrivals after a quiet minute is admitted at the pace too.
/// </summary>
public sealed partial class AdmissionSystem : EcsSystem
{
    [ForEach]
    private static void Admit(ref WaitingRoom room, World world)
    {
        long now = world.Time.GetUtcNow().ToUnixTimeMilliseconds();
        if (room.Released >= room.Issued)
        {
            room.AtMs = now;
            return;
        }

        long due = (now - room.AtMs) * room.PerSecond / 1000;
        if (due <= 0) return;
        room.AtMs += due * 1000 / room.PerSecond;
        room.Released = Math.Min(room.Issued, room.Released + due);
    }
}
