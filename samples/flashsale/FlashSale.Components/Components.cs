using DaServer;
using EasyEcs;
using FlashSale.Contracts;
using Nino.Core;

namespace FlashSale.Components;

/// <summary>Units left in a stock shard and who took the others: stored, so a restart sells no unit twice.</summary>
[NinoType, Persist] public partial record struct Stock(int Left, List<long> Buyers) : IComponent;

/// <summary>What a buyer's order came to: stored, so a retry after a restart gets the same answer.</summary>
[NinoType, Persist] public partial record struct Purchase(OrderStatus Status) : IComponent;

/// <summary>
/// A sale: its queue if it has one (positions handed out and admitted, the pace per second, the time admissions are
/// counted from), the stock shards known empty (a bit each) and who bought. Stored, so a restart neither forgets the
/// queue nor admits it again.
/// </summary>
[NinoType, Persist]
public partial record struct SaleState(int PerSecond, long Issued, long Released, long AtMs, int EmptyShards, List<long> Buyers) : IComponent
{
    public readonly bool Queued => PerSecond > 0;

    public readonly bool SoldOut => EmptyShards == (1 << StockShardId.Shards) - 1;
}

/// <summary>
/// The sales a shard's gate has heard about (asked once per shard): sold-out sales and their buyers are answered right
/// there, so the late crowd spreads over every shard instead of queueing at one actor.
/// </summary>
public readonly record struct KnownSales(Dictionary<long, SaleInfo> Sales) : ISingletonComponent;
