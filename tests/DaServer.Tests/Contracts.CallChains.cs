using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- call chains (actors asking each other)

public readonly record struct LoopId(long Value) : IActorId;

/// <summary>
/// Ask <see cref="Path"/>'s first actor to ask the rest in turn (<see cref="ViaService"/>: through
/// <see cref="LoopService"/>); the last one answers. A path that returns to an actor still waiting is a call cycle.
/// </summary>
[NinoType, Message] public partial record struct Hop(long[] Path, int At, bool ViaService) : IRequest<int>;

/// <summary>Hold the turn for <see cref="Ms"/> (another caller's request queues behind it).</summary>
[NinoType, Message] public partial record struct Hold(int Ms) : IRequest<int>;

/// <summary>The actor's TimeLeft after an await of its own (it holds for the whole turn), in ms.</summary>
[NinoType, Message] public partial record struct HowLongAfterAwait : IRequest<long>;

public sealed class Looper : Actor<LoopId>
{
    /// <summary>Keys whose unload never finishes (a save that hangs): the stop budget test.</summary>
    public const long HangsOnUnload = 999;

    /// <summary>Keys whose OnLoad asks the actor 1,000 below them (the one whose request loads them: a cycle).</summary>
    public static bool AsksBackOnLoad(long key) => key is >= 2_000 and < 3_000;

    /// <summary>Keys that unload when idle, asking the actor 1,000 above them to ask them back (a cycle).</summary>
    public static bool AskedBackOnUnload(long key) => key is >= 3_000 and < 4_000;

    public override TimeSpan UnloadAfterIdle => AskedBackOnUnload(Key) ? TimeSpan.FromMilliseconds(100) : base.UnloadAfterIdle;

    public override async Reply OnLoad()
    {
        if (AsksBackOnLoad(Key)) await Cluster.Ask(new LoopId(Key - 1_000), new Hold(0));
    }

    public override async Reply OnUnload()
    {
#pragma warning disable DAS009 // on purpose: a save that does not finish within the stop budget
        if (Key == HangsOnUnload) await Task.Delay(Timeout.Infinite);
#pragma warning restore DAS009
        if (!AskedBackOnUnload(Key)) return;
        try
        {
            await Cluster.Ask(new LoopId(Key + 1_000), new Hop([Key + 1_000, Key], 0, ViaService: false));
        }
        finally
        {
            Observed.Events.Enqueue($"looper-unloaded:{Key}");
        }
    }

    [Handle]
    public async Reply<int> On(Hop m)
    {
        if (m.At == m.Path.Length - 1) return m.At;
        var next = m with { At = m.At + 1 };
        return m.ViaService ? await Cluster.Ask(new PassOn(next)) : await Cluster.Ask(new LoopId(m.Path[next.At]), next);
    }

    [Handle]
    public async Reply<long> On(HowLongAfterAwait _)
    {
        await Cluster.Ask(new LoopId(Key + 1), new Hold(50));
        return (long)TimeLeft.TotalMilliseconds;
    }

    [Handle]
    public async Reply<int> On(Hold m)
    {
#pragma warning disable DAS009 // on purpose: a turn that holds its actor
        await Task.Delay(m.Ms);
#pragma warning restore DAS009
        return m.Ms;
    }
}

/// <summary>A hop through <see cref="LoopService"/>.</summary>
[NinoType, Message] public partial record struct PassOn(Hop Hop) : IRequest<int>;

/// <summary>Passes a hop on: a service in the middle of a chain keeps it.</summary>
public sealed class LoopService : Service
{
    [Handle] public Reply<int> On(PassOn m) => Cluster.Ask(new LoopId(m.Hop.Path[m.Hop.At]), m.Hop);
}
