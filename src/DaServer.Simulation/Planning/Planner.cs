using System.Runtime.CompilerServices;
using EasyEcs;

namespace DaServer.Simulation.Planning;

// Hierarchical task network planning as ECS data. What an agent believes is a bit set (Facts, written by sensor
// systems); what it can do is a domain singleton of plain tables (tasks, methods, steps; conditions and effects are bit
// masks); its plan is a component of task ids. PlannerSystem decomposes the root task on a copy of the facts, trying
// the methods of each compound task in order and backtracking when one cannot be completed, and starts the plan's
// tasks one by one. A primitive task is a component: it starts by being added and is done when the system that
// carries it out removes it. A priority-ordered behavior selector (ET's AI handlers) is the special case of a root with
// one task per method. Planning reads and writes only these components: it allocates nothing.

/// <summary>What an agent believes now (bits of a fact enum), written by sensor systems.</summary>
public record struct Facts(ulong Bits) : IComponent;

/// <summary>The task ids of an agent's current plan, the next one to start and the running one (-1: none).</summary>
public struct Plan() : IComponent
{
    public const int MaxSteps = 8;

    public StepBuffer Steps;
    public int Count, Next;
    public int Running = -1;

    [InlineArray(MaxSteps)]
    public struct StepBuffer
    {
        private int _step;
    }
}

/// <summary>
/// Moves every agent along its plan: when its task is done, starts the next one whose condition still holds, or plans
/// again from the root (nothing applies: it tries again next tick).
/// </summary>
public sealed partial class PlannerSystem : EcsSystem
{
    [ForEach]
    private static void Advance(Entity agent, ref Plan plan, in Facts facts, in HtnDomain domain)
    {
        if (plan.Running >= 0 && domain.Running[plan.Running](agent.World, agent.Id)) return;
        plan.Running = -1;
        if (plan.Next >= plan.Count || !Applies(domain, plan.Steps[plan.Next], facts.Bits))
        {
            ulong planning = facts.Bits;
            (plan.Count, plan.Next) = (0, 0);
            if (!domain.Decompose(domain.Root, ref planning, ref plan)) return;
        }

        int task = plan.Steps[plan.Next++];
        plan.Running = task;
        domain.Start[task](agent.World, agent.Id); // last: `plan` is invalid after it
    }

    private static bool Applies(in HtnDomain domain, int task, ulong facts)
    {
        var t = domain.Tasks[task];
        return HtnDomain.Holds(facts, t.Requires, t.Forbids);
    }
}
