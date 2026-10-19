using DaServer;
using EasyEcs;
using FlashSale.Contracts;
using Nino.Core;

namespace FlashSale.Components;

// ---------------------------------------------------------------- the sale's actors

/// <summary>Units left in a stock shard and who took the others: stored, so a restart sells no unit twice.</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Stock(int Left, List<long> Buyers) : IComponent;

/// <summary>What a buyer's order came to: stored, so a retry after a restart gets the same answer.</summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Purchase(OrderStatus Status) : IComponent;

/// <summary>
/// A purchase in progress: the order is taking a unit, <see cref="Tried"/> shards found empty so far (its own shard
/// first, then the next ones). Present only while it runs; moved with the order if it changes node meanwhile, so the
/// answer of the shard it asked still lands on a purchase that knows where it was.
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant), Handoff] public partial record struct Buying(int Tried) : IComponent;

/// <summary>
/// What a sale has sold: the stock shards known empty (a bit each) and who bought their units. Stored, so a restart
/// does not forget a sold-out sale's buyers.
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist]
public partial record struct Sold(int EmptyShards, List<long> Buyers) : IComponent
{
    public readonly bool SoldOut => EmptyShards == (1 << StockShardId.Shards) - 1;
}

/// <summary>
/// A queued sale's waiting room (only queued sales have one): positions handed out and admitted, the pace per second
/// and the time admissions are counted from. Stored, so a restart neither forgets the queue nor admits it again.
/// </summary>
[NinoType(format: NinoFormat.VersionTolerant), Persist]
public partial record struct WaitingRoom(int PerSecond, long Issued, long Released, long AtMs) : IComponent;

// ---------------------------------------------------------------- the gate's world

/// <summary>
/// A sale a shard's gate has heard about (asked once per shard, found by its number): a sold-out sale and its buyers
/// are answered right there, so the late crowd spreads over every shard instead of queueing at one actor.
/// </summary>
public readonly record struct KnownSale(long Sale, SaleInfo Info) : IComponent, IIndexed<long>
{
    long IIndexed<long>.Key => Sale;
}

// A client request the gate serves in steps (asks of the sale's actors) is an entity while it waits: these components
// hold what its next step needs, and go with it when it is answered.

/// <summary>A <see cref="PlaceOrder"/> being served: learning the sale, then checking the queue, then buying.</summary>
public readonly record struct Placing(PlaceOrder Order) : IComponent;

/// <summary>A <see cref="QueueUp"/> being served: learning the sale, then joining its line.</summary>
public readonly record struct QueueingUp(QueueUp Request) : IComponent;

/// <summary>A <see cref="CheckIn"/> being served: asking the sale.</summary>
public readonly record struct CheckingIn(CheckIn Request) : IComponent;

/// <summary>An <see cref="OpenSale"/> being served: opening the sale, then stocking its shards.</summary>
public readonly record struct Opening(OpenSale Request) : IComponent;

/// <summary>The answers of the stock shards so far (every shard is asked at once) and what they summed to.</summary>
public record struct ShardAnswers(int Count, int Sum) : IComponent;
