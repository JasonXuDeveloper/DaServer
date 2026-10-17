using DaServer;
using EasyEcs;
using FlashSale.Contracts;
using Nino.Core;

namespace FlashSale.Components;

/// <summary>Units left in a stock shard: stored, so a restart sells no unit twice.</summary>
[NinoType, Persist] public partial record struct Stock(int Left) : IComponent;

/// <summary>What a buyer's order came to: stored, so a retry after a restart gets the same answer.</summary>
[NinoType, Persist] public partial record struct Purchase(OrderStatus Status) : IComponent;

/// <summary>
/// A waiting room: positions handed out, positions admitted, the pace (per second), the time admissions are counted
/// from, and the stock shards known empty (a bit each). Stored, so a restart neither forgets the queue nor admits it
/// again.
/// </summary>
[NinoType, Persist] public partial record struct Room(long Issued, long Released, int PerSecond, long AtMs, int EmptyShards) : IComponent
{
    public readonly bool SoldOut => EmptyShards == (1 << Sale.Shards) - 1;
}
