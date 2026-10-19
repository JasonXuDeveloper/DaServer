using BenchmarkDotNet.Attributes;
using DaServer.Benchmark.MapTick;

namespace DaServer.Benchmark;

/// <summary>
/// One server tick of a crowded map, written two ways over the same scenario (MapScenario): the usual object per
/// avatar with a virtual Update that tells viewers at once, and an ECS pipeline that moves everyone in a SIMD batch,
/// updates who sees whom only for avatars that changed cell, and sends each busy cell's changes as one frame. Both
/// deliver the same events to the same clients; the ECS map packs a cell's events into one frame per viewer.
/// </summary>
[MemoryDiagnoser]
public class MapTickBenchmark
{
    /// <summary>Avatars on the map (about ten per cell, each a client that sees the 3x3 cells around it).</summary>
    [Params(1_000, 10_000)]
    public int Avatars;

    /// <summary>Avatars that change direction in a tick, per thousand.</summary>
    [Params(50, 500)]
    public int TurnPermille;

    private TraditionalMap _traditional = null!;
    private EcsMap _ecs = null!;

    [GlobalSetup]
    public void Setup()
    {
        Verify();
        _traditional = new TraditionalMap(new MapScenario(Avatars, TurnPermille));
        _ecs = new EcsMap(new MapScenario(Avatars, TurnPermille));
    }

    /// <summary>
    /// Both maps tell the clients the same things: over the same ticks they send nearly the same event bytes (a
    /// velocity change sent before or after a cell change in the same tick may reach a few viewers more or less).
    /// </summary>
    private void Verify()
    {
        var a = new MapScenario(Avatars, TurnPermille);
        var b = new MapScenario(Avatars, TurnPermille);
        var traditional = new TraditionalMap(a);
        using var ecs = new EcsMap(b);
        for (int i = 0; i < 100; i++)
        {
            traditional.Tick();
            ecs.Tick();
        }

        var (tf, tb) = a.Totals();
        var (ef, eb) = b.Totals();
        Console.WriteLine($"// MapTick check: traditional {tf} frames {tb} B, ECS {ef} frames {eb} B");
        if (tb == 0 || Math.Abs(tb - eb) > tb / 20)
            throw new InvalidOperationException($"the maps disagree: traditional {tb} B, ECS {eb} B");
    }

    [GlobalCleanup]
    public void Cleanup() => _ecs.Dispose();

    [Benchmark(Baseline = true)]
    public void Traditional() => _traditional.Tick();

    [Benchmark]
    public void Ecs() => _ecs.Tick();
}
