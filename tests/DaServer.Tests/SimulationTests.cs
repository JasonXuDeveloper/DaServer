using DaServer.Simulation;
using DaServer.Simulation.Planning;
using EasyEcs;

namespace DaServer.Tests;

[Flags]
public enum Need : ulong
{
    None = 0,
    Hungry = 1,
    HasFood = 2,
    Tired = 4,
}


/// <summary>Test agents' world: needs change every tick and every task is done in one, so every agent plans every tick.</summary>
public sealed partial class NeedsSystem : EcsSystem
{
    public override int Priority => -40;

    [ForEach]
    private static void Change(ref Facts facts) => facts = new Facts((facts.Bits + 1) % 8);
}

public sealed partial class ChoresSystem : EcsSystem
{
    [ForEach, With<Fetch>] private static void Fetched(Entity agent) => agent.Remove<Fetch>();
    [ForEach, With<Eat>] private static void Ate(Entity agent) => agent.Remove<Eat>();
    [ForEach, With<Nap>] private static void Napped(Entity agent) => agent.Remove<Nap>();
    [ForEach, With<Idle>] private static void Idled(Entity agent) => agent.Remove<Idle>();
}

/// <summary>Simulated users (DaServer.Simulation): HTN planning as ECS data, client loops, swarms against a cluster.</summary>
public class SimulationTests : ClusterTest
{
    private static (HtnDomain Domain, int Fetch, int Eat, int Nap, int Idle) Domain()
    {
        var d = new DomainBuilder<Need>();
        int fetch = d.Primitive<Fetch>(forbids: Need.HasFood, sets: Need.HasFood);
        int eat = d.Primitive<Eat>(requires: Need.Hungry | Need.HasFood, clears: Need.Hungry | Need.HasFood);
        int nap = d.Primitive<Nap>(requires: Need.Tired, clears: Need.Tired);
        int idle = d.Primitive<Idle>();
        int feed = d.Compound();
        d.Method(feed, Need.HasFood, eat).Method(feed, Need.None, fetch, eat);
        int live = d.Compound();
        d.Method(live, Need.Hungry, feed, nap) // only when also tired: else it backtracks to the next method
            .Method(live, Need.Hungry, feed)
            .Method(live, Need.None, idle);
        return (d.Build(live), fetch, eat, nap, idle);
    }

    private static int[] PlanFor(HtnDomain domain, Need facts)
    {
        var plan = new Plan();
        ulong bits = (ulong)facts;
        Assert.That(domain.Decompose(domain.Root, ref bits, ref plan), Is.True);
        var steps = new int[plan.Count];
        for (int i = 0; i < plan.Count; i++) steps[i] = plan.Steps[i];
        return steps;
    }

    [Test]
    public void PlansFollowMethodOrderEffectsAndBacktracking()
    {
        var (domain, fetch, eat, nap, idle) = Domain();
        Assert.That(PlanFor(domain, Need.Hungry | Need.Tired), Is.EqualTo(new[] { fetch, eat, nap }), "fetching sets HasFood for eating");
        Assert.That(PlanFor(domain, Need.Hungry), Is.EqualTo(new[] { fetch, eat }), "not tired: the first method fails late and backtracks");
        Assert.That(PlanFor(domain, Need.Hungry | Need.HasFood), Is.EqualTo(new[] { eat }));
        Assert.That(PlanFor(domain, Need.None), Is.EqualTo(new[] { idle }));
    }

    [Test]
    public void ThePlannerRunsTasksAsComponentsAndPlansAgain()
    {
        var (domain, _, _, _, _) = Domain();
        using var world = new World();
        world.AddSingleton<HtnDomain>() = domain;
        world.AddSystem<PlannerSystem>();
        var agent = world.CreateEntity(new Facts((ulong)Need.Hungry), new Plan());

        world.Update();
        Assert.That(agent.Has<Fetch>(), Is.True, "the first task starts as its component");
        world.Update();
        Assert.That(agent.Has<Eat>(), Is.False, "a task runs until its component is gone");

        agent.Remove<Fetch>(); // the fetching system is done
        agent.Get<Facts>() = new Facts((ulong)(Need.Hungry | Need.HasFood)); // what the sensors see now
        world.Update();
        Assert.That(agent.Has<Eat>(), Is.True);

        agent.Remove<Eat>();
        agent.Get<Facts>() = new Facts((ulong)Need.Tired);
        world.Update();
        Assert.That(agent.Has<Nap>() || agent.Has<Idle>(), Is.True, "the plan is done: it plans again");
        Assert.That(agent.Has<Idle>(), Is.True, "napping is part of feeding, not a reason on its own");

        agent.Remove<Idle>();
        agent.Get<Facts>() = new Facts((ulong)Need.Hungry);
        agent.Get<Plan>().Count = 2; // a stale plan whose next step (eat) no longer applies
        agent.Get<Plan>().Next = 1;
        world.Update();
        Assert.That(agent.Has<Fetch>(), Is.True, "a next step that no longer applies makes it plan again");
    }

    [Test]
    public void PlanningAllocatesNothing()
    {
        // 1,000 agents planning and finishing a task every tick: the worst case, in steady state.
        using var world = new World();
        world.AddSingleton<HtnDomain>() = Domain().Domain;
        world.AddSystem<NeedsSystem>();
        world.AddSystem<PlannerSystem>();
        world.AddSystem<ChoresSystem>();
        for (int i = 0; i < 1000; i++) world.CreateEntity(new Facts((ulong)(i % 8)), new Plan());
        for (int i = 0; i < 200; i++) world.Update(); // warm-up: archetypes for every task component

        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int i = 0; i < 1000; i++) world.Update();
        long perAgentTick = (GC.GetTotalAllocatedBytes(precise: true) - before) / (1000L * 1000);
        Assert.That(perAgentTick, Is.LessThan(1), "bytes per agent and tick");
    }

    [Test]
    public async Task ClientLoopsResumeAwaitsOnTheirThread()
    {
        using var loop = new ClientLoop("test-loop");
        bool same = await loop.Run(async () =>
        {
            var thread = Thread.CurrentThread;
            await Task.Delay(20);
            return Thread.CurrentThread == thread && thread.Name == "test-loop";
        });
        Assert.That(same, Is.True);
    }

    [Test]
    public async Task ASwarmStartsUsersAndReadsTheirWorlds()
    {
        var gateway = await Start("gw", gateway: true);
        await using var swarm = new Swarm(new SwarmOptions
        {
            Loops = 2,
            Configure = static (world, meter) => meter.Counter("logins"),
        });
        var logins = swarm.Meter.Counter("logins");
        await swarm.StartAsync(40, static () => new ClusterClient(), async user =>
        {
            await user.Client.ConnectAsync("127.0.0.1", gateway.ClientPort());
            if (await user.Client.Ask(new Login(new PlayerId(7000 + user.Index)))) logins.Add();
            user.World.CreateEntity(new Facts(1));
        });

        Assert.That(await swarm.SumAsync(Users), Is.EqualTo(40), "each user is an entity of one loop's world");
        Assert.That(logins.Value, Is.EqualTo(40));
        Assert.That(swarm.Meter.Counters.Count(c => c.Name == "logins"), Is.EqualTo(1), "a counter name is one counter");
        Assert.That(swarm.Meter.Disconnects.Value, Is.Zero);

        static long Users(World world)
        {
            long n = 0;
            foreach (var _ in world.Query<Facts>()) n++;
            return n;
        }
    }
}
