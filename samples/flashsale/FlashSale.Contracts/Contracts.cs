using DaServer;
using Nino.Core;

namespace FlashSale.Contracts;

// A flash sale: a few thousand units, tens of thousands of buyers at the same second. Buyers wait in the sale's waiting
// room, which admits them at the pace the sale can take; the stock is split into shards, each an actor, so buyers
// spread over actors and nodes; each buyer's order is an actor, so a retried purchase gets the same answer.

/// <summary>Per-sale constants shared by every node.</summary>
public static class Sale
{
    /// <summary>Stock shards per sale: buyers start at their own shard and move on when it is empty.</summary>
    public const int Shards = 16;
}

/// <summary>A shard of a sale's stock: sale number in the high bits.</summary>
public readonly record struct StockShardId(long Value) : IActorId
{
    public static StockShardId Of(long sale, int shard) => new(sale << 8 | (uint)shard);

    public long Sale => Value >> 8;

    public int Shard => (int)(Value & 0xFF);
}

/// <summary>A buyer's order in a sale: the user in the high bits, the sale (under 65,536) in the low ones.</summary>
public readonly record struct OrderId(long Value) : IActorId
{
    public static OrderId Of(long sale, long user) => new(user << 16 | sale);

    public long Sale => Value & 0xFFFF;

    public long User => Value >> 16;
}

/// <summary>A sale's waiting room (one per sale).</summary>
public readonly record struct WaitingRoomId(long Value) : IActorId;

public enum OrderStatus : byte
{
    None,
    Confirmed,
    SoldOut,
}

public enum Admission : byte
{
    Waiting,
    Admitted,
    SoldOut,
}

/// <summary>Set a stock shard's units.</summary>
[NinoType, Message] public partial record struct Restock(int Units) : IRequest<bool>;

/// <summary>Take one unit of a stock shard: false when it is empty.</summary>
[NinoType, Message] public partial record struct TakeOne : IRequest<bool>;

[NinoType, Message] public partial record struct UnitsLeft : IRequest<int>;

/// <summary>Buy one unit (to the buyer's <see cref="OrderId"/>): the outcome, the same on every retry.</summary>
[NinoType, Message] public partial record struct Buy : IRequest<OrderStatus>;

[NinoType, Message] public partial record struct GetOrder : IRequest<OrderStatus>;

/// <summary>Open the waiting room, admitting <see cref="PerSecond"/> buyers a second.</summary>
[NinoType, Message] public partial record struct OpenRoom(int PerSecond) : IRequest<bool>;

/// <summary>Take a place in the queue: the buyer's position, or 0 when the sale is sold out.</summary>
[NinoType, Message] public partial record struct Join : IRequest<long>;

/// <summary>Whether the buyer at <see cref="Position"/> may buy now.</summary>
[NinoType, Message] public partial record struct Admitted(long Position) : IRequest<Admission>;

/// <summary>A stock shard ran out (to the sale's waiting room): when all have, the room turns buyers away.</summary>
[NinoType, Message] public partial record struct ShardEmpty(int Shard);
