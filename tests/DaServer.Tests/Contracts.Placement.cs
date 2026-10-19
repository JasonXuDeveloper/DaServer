using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- placement affinity (ADR-073)

/// <summary>A table, placed by its number.</summary>
public readonly record struct TableId(long Value) : IActorId, IPlaced
{
    public long Placement => Value;
}

/// <summary>A chair at a table: table number in the high bits, placed with its table.</summary>
public readonly record struct ChairId(long Value) : IActorId, IPlaced
{
    public static ChairId Of(long table, int chair) => new(table << 16 | (uint)chair);

    public long Placement => Value >> 16;
}

[NinoType, Message] public partial record struct WhereIsIt : IRequest<string>;

[Actor<TableId>]
public static class Table
{
    [Handle] internal static string On(in WhereIsIt _) => Thread.CurrentThread.Name ?? "";
}

[Actor<ChairId>]
public static class Chair
{
    [Handle] internal static string On(in WhereIsIt _) => Thread.CurrentThread.Name ?? "";
}
