using BenchmarkDotNet.Attributes;
using DaServer.Simulation.Planning;
using EasyEcs;

namespace DaServer.Benchmark;

[Flags]
public enum Need : ulong
{
    None = 0,
    Hungry = 1,
    HasFood = 2,
    Tired = 4,
}



public sealed partial class NeedsSystem : EcsSystem
{
    public override int Priority => -40;

    [ForEach]
    private static void Change(ref Facts facts, in Restless restless)
    {
        if (restless.Changing) facts = new Facts((facts.Bits + 1) % 8);
    }
}

/// <summary>Replanning: every task is done in one tick. Running: tasks never end (agents only check them).</summary>
public sealed partial class ChoresSystem : EcsSystem
{
    [ForEach, With<Fetch>] private static void Fetched(Entity agent, in Restless r) { if (r.Changing) agent.Remove<Fetch>(); }
    [ForEach, With<Eat>] private static void Ate(Entity agent, in Restless r) { if (r.Changing) agent.Remove<Eat>(); }
    [ForEach, With<Nap>] private static void Napped(Entity agent, in Restless r) { if (r.Changing) agent.Remove<Nap>(); }
    [ForEach, With<Idle>] private static void Idled(Entity agent, in Restless r) { if (r.Changing) agent.Remove<Idle>(); }
}

/// <summary>
/// What simulated users' behavior costs a client loop: one tick of 10,000 agents planned with HTN as ECS data
/// (DaServer.Simulation). Replanning: every agent finishes a task and plans again every tick (the worst case);
/// Running: every agent's task is still running (the common tick).
/// </summary>
[MemoryDiagnoser]
public class PlannerBenchmark
{
    private const int Agents = 10_000;
    private World _world = null!;

    [Params(true, false)]
    public bool Replanning { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var d = new DomainBuilder<Need>();
        int fetch = d.Primitive<Fetch>(forbids: Need.HasFood, sets: Need.HasFood);
        int eat = d.Primitive<Eat>(requires: Need.Hungry | Need.HasFood, clears: Need.Hungry | Need.HasFood);
        int nap = d.Primitive<Nap>(requires: Need.Tired, clears: Need.Tired);
        int idle = d.Primitive<Idle>();
        int feed = d.Compound();
        d.Method(feed, Need.HasFood, eat).Method(feed, Need.None, fetch, eat);
        int live = d.Compound();
        d.Method(live, Need.Hungry, feed, nap).Method(live, Need.Hungry, feed).Method(live, Need.None, idle);

        _world = new World();
        _world.AddSingleton<HtnDomain>() = d.Build(live);
        _world.AddSystem<NeedsSystem>();
        _world.AddSystem<PlannerSystem>();
        _world.AddSystem<ChoresSystem>();
        for (int i = 0; i < Agents; i++) _world.CreateEntity(new Facts((ulong)(i % 8)), new Plan(), new Restless(Replanning));
        for (int i = 0; i < 100; i++) _world.Update();
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark(OperationsPerInvoke = Agents)]
    public void Tick() => _world.Update();
}
