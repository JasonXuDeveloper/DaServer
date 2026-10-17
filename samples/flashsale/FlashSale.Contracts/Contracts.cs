using DaServer;
using Nino.Core;

namespace FlashSale.Contracts;

// A flash sale: a few units, a crowd at the same second. Buyers connect to any node (the platform's load balancer
// spreads them) and talk to its stateless Gate service; the stock is split into shards, each an actor, so buyers spread
// over actors and nodes; each buyer's order is an actor, so a retried purchase gets the same answer; the sale's actor
// knows when every shard is empty and who bought, so the late crowd is answered where it arrives. A sale may also
// queue its buyers (a waiting room admitting them at a pace).

/// <summary>A sale (its number under 65,536).</summary>
public readonly record struct SaleId(long Value) : IActorId;

/// <summary>A shard of a sale's stock: sale number in the high bits.</summary>
public readonly record struct StockShardId(long Value) : IActorId
{
    /// <summary>Stock shards per sale: buyers start at their own shard and move on when it is empty.</summary>
    public const int Shards = 16;

    public static StockShardId Of(long sale, int shard) => new(sale << 8 | (uint)shard);

    public long Sale => Value >> 8;

    public int Shard => (int)(Value & 0xFF);
}

/// <summary>A buyer's order in a sale: the user in the high bits, the sale in the low ones.</summary>
public readonly record struct OrderId(long Value) : IActorId
{
    public static OrderId Of(long sale, long user) => new(user << 16 | sale);

    public long Sale => Value & 0xFFFF;

    public long User => Value >> 16;
}

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

/// <summary>
/// What a gate needs to know about a sale: whether it queues its buyers, whether it is sold out and, then, who bought
/// (so a buyer retrying after the sale ended still hears "confirmed").
/// </summary>
[NinoType] public partial record struct SaleInfo(bool Queued, bool SoldOut, long[] Buyers);

// Between the sale's actors.

/// <summary>Open the sale; <see cref="PerSecond"/> above 0 queues its buyers, admitting that many a second.</summary>
[NinoType, Message] public partial record struct Open(int PerSecond) : IRequest<bool>;

[NinoType, Message] public partial record struct Describe : IRequest<SaleInfo>;

/// <summary>Set a stock shard's units.</summary>
[NinoType, Message] public partial record struct Restock(int Units) : IRequest<bool>;

/// <summary>Take one unit for <see cref="User"/>: false when the shard is empty.</summary>
[NinoType, Message] public partial record struct TakeOne(long User) : IRequest<bool>;

[NinoType, Message] public partial record struct UnitsLeft : IRequest<int>;

/// <summary>A stock shard ran out (to the sale): who bought its units.</summary>
[NinoType, Message] public partial record struct ShardEmpty(int Shard, long[] Buyers);

/// <summary>Buy one unit (to the buyer's <see cref="OrderId"/>): the outcome, the same on every retry.</summary>
[NinoType, Message] public partial record struct Buy : IRequest<OrderStatus>;

[NinoType, Message] public partial record struct GetOrder : IRequest<OrderStatus>;

/// <summary>Take a place in a queued sale: the position, or 0 when it is sold out.</summary>
[NinoType, Message] public partial record struct Join : IRequest<long>;

/// <summary>Whether the buyer at <see cref="Position"/> of a queued sale may buy now.</summary>
[NinoType, Message] public partial record struct Admitted(long Position) : IRequest<Admission>;

// What clients send (TCP, or WebSocket for browsers), handled by the Gate service on whichever node they reach.

/// <summary>
/// Open a sale: its stock split over the shards; <see cref="PerSecond"/> above 0 queues its buyers. The sample's crowd
/// opens its own sale; a real shop does it from its back office (an admin endpoint, ADR-046).
/// </summary>
[NinoType, Message, FromClient] public partial record struct OpenSale(long Sale, int Units, int PerSecond) : IRequest<bool>;

/// <summary>Buy as <see cref="User"/> (<see cref="Position"/>: the admitted place in a queued sale, else 0): the outcome, the same on every retry.</summary>
[NinoType, Message, FromClient] public partial record struct PlaceOrder(long Sale, long User, long Position) : IRequest<OrderStatus>;

/// <summary>Take a place in a queued sale's line: the position, or 0 when it is sold out.</summary>
[NinoType, Message, FromClient] public partial record struct QueueUp(long Sale) : IRequest<long>;

/// <summary>Whether the buyer at <see cref="Position"/> may buy now.</summary>
[NinoType, Message, FromClient] public partial record struct CheckIn(long Sale, long Position) : IRequest<Admission>;

/// <summary>Units left in the sale (the shards summed).</summary>
[NinoType, Message, FromClient] public partial record struct SaleStock(long Sale) : IRequest<int>;

/// <summary>What <see cref="User"/>'s order in the sale came to.</summary>
[NinoType, Message, FromClient] public partial record struct OrderOf(long Sale, long User) : IRequest<OrderStatus>;
