using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- visitor (actor unloaded when idle)

[NinoType, Message] public partial record struct Visit : IRequest<int>;


public sealed class Visitor : Actor<VisitorId, Visits>
{
    public override TimeSpan UnloadAfterIdle => TimeSpan.FromMilliseconds(300);

    public override Reply OnLoad()
    {
        Observed.Events.Enqueue($"load:{Id.Value}");
        return default;
    }

    public override Reply OnUnload()
    {
        Observed.Events.Enqueue($"unload:{Id.Value}");
        return default;
    }

    [Handle] public int On(Visit _) => ++State.Count;
}

/// <summary>A visitor that is never unloaded for idleness: only removal paths unload it.</summary>
public readonly record struct KeeperId(long Value) : IActorId;

public sealed class Keeper : Actor<KeeperId, Visits>
{
    public override Reply OnUnload()
    {
        Observed.Events.Enqueue($"keeper-unload:{Id.Value}");
        return default;
    }

    [Handle] public int On(Visit _) => ++State.Count;
}
