using DaServer;
using DaServer.Rpc;
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
        State.Left = m.Units;
        Entity.GetOrAdd<SaveNow>();
        if (m.Units == 0) Empty();
        return true;
    }

    [Handle]
    public bool On(TakeOne _)
    {
        if (State.Left == 0) return false;
        if (--State.Left == 0) Empty();
        Entity.GetOrAdd<SaveNow>(); // a sold unit is written before the next restart could sell it again
        return true;
    }

    /// <summary>Tell the waiting room: once every shard is empty it turns buyers away instead of queueing them.</summary>
    private void Empty() => Cluster.Send(new WaitingRoomId(Id.Sale), new ShardEmpty(Id.Shard));

    [Handle] public int On(UnitsLeft _) => State.Left;
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
        for (int i = 0; i < Sale.Shards; i++) // the buyer's own shard first, then the next ones as they run out
        {
            var shard = StockShardId.Of(Id.Sale, (int)((Id.User + i) % Sale.Shards));
            if (await Cluster.Ask(shard, new TakeOne()))
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
/// A sale's waiting room: buyers take a position, <see cref="AdmissionSystem"/> admits positions at the sale's pace,
/// and a buyer may buy once its position is admitted. Once every stock shard is empty it turns everyone away, so the
/// crowd that comes too late costs one answer each (and none at all from the edges that heard it, Endpoints.cs).
/// </summary>
public sealed class WaitingRoom : Actor<WaitingRoomId, Room>
{
    [Handle]
    public bool On(OpenRoom m)
    {
        State = new Room(0, 0, m.PerSecond, Time.GetUtcNow().ToUnixTimeMilliseconds(), 0); // before the shards are stocked
        Entity.GetOrAdd<SaveNow>();
        return true;
    }

    [Handle] public long On(Join _) => State.SoldOut ? 0 : ++State.Issued;

    [Handle]
    public Admission On(Admitted m) =>
        State.SoldOut ? Admission.SoldOut : m.Position <= State.Released ? Admission.Admitted : Admission.Waiting;

    [Handle]
    public void On(ShardEmpty m)
    {
        State.EmptyShards |= 1 << m.Shard;
        if (State.SoldOut) Entity.GetOrAdd<SaveNow>();
    }
}

/// <summary>
/// Admits waiting buyers at each room's pace, every tick. Capacity nobody waited for is not saved up: a burst of
/// arrivals after a quiet minute is admitted at the pace too.
/// </summary>
public sealed partial class AdmissionSystem : EcsSystem
{
    [ForEach]
    private static void Admit(ref Room room, World world)
    {
        long now = world.Time().GetUtcNow().ToUnixTimeMilliseconds();
        if (room.PerSecond <= 0 || room.Released >= room.Issued)
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
