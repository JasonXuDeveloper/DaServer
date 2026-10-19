using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- visitor (actor unloaded when idle)

[NinoType, Message] public partial record struct Visit : IRequest<int>;

/// <summary>Unloaded a second after its last mail; it reports its loads and unloads.</summary>
[Actor<VisitorId>(UnloadAfterIdleSeconds = 1)]
public static class Visitor
{
    [Handle] internal static void On(in Created _, in Context ctx) => Observed.Events.Enqueue($"load:{ctx.Key}");

    [Handle] internal static void On(in Loaded _, in Context ctx) => Observed.Events.Enqueue($"load:{ctx.Key}");

    [Handle] internal static void On(in Unloading _, in Context ctx) => Observed.Events.Enqueue($"unload:{ctx.Key}");

    [Handle] internal static int On(in Visit _, ref Visits visits) => ++visits.Count;
}

/// <summary>A visitor that is never unloaded for idleness: only removal paths unload it.</summary>
public readonly record struct KeeperId(long Value) : IActorId;

[Actor<KeeperId>]
public static class Keeper
{
    [Handle] internal static void On(in Unloading _, in Context ctx) => Observed.Events.Enqueue($"keeper-unload:{ctx.Key}");

    [Handle] internal static int On(in Visit _, ref Visits visits) => ++visits.Count;
}
