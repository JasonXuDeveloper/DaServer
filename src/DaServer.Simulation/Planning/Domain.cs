using EasyEcs;

namespace DaServer.Simulation.Planning;

/// <summary>A task: primitive (<see cref="FirstMethod"/> -1) with its condition and effect, or compound with its methods.</summary>
public readonly record struct TaskEntry(ulong Requires, ulong Forbids, ulong Sets, ulong Clears, int FirstMethod, int Methods);

/// <summary>A method of a compound task: its condition and its steps (a range of the step table).</summary>
public readonly record struct MethodEntry(ulong Requires, ulong Forbids, int FirstStep, int Steps);

/// <summary>
/// A planning domain (built once, <see cref="DomainBuilder{TFact}"/>): tables of tasks, methods and steps, and per
/// primitive task how to start it (add its component) and whether it still runs (has it).
/// </summary>
public readonly record struct HtnDomain(int Root, TaskEntry[] Tasks, MethodEntry[] Methods, int[] Steps,
    Action<Entity>[] Start, Func<Entity, bool>[] Running) : ISingletonComponent
{
    /// <summary>Decompose task <paramref name="task"/> into <paramref name="plan"/> on <paramref name="facts"/> (a copy).</summary>
    public bool Decompose(int task, ref ulong facts, ref Plan plan)
    {
        var t = Tasks[task];
        if (t.FirstMethod < 0)
        {
            if (!Holds(facts, t.Requires, t.Forbids) || plan.Count == Plan.MaxSteps) return false;
            plan.Steps[plan.Count++] = task;
            facts = (facts | t.Sets) & ~t.Clears;
            return true;
        }

        for (int m = t.FirstMethod; m < t.FirstMethod + t.Methods; m++)
        {
            var method = Methods[m];
            if (!Holds(facts, method.Requires, method.Forbids)) continue;
            (ulong before, int mark) = (facts, plan.Count);
            bool done = true;
            for (int s = method.FirstStep; s < method.FirstStep + method.Steps && done; s++)
                done = Decompose(Steps[s], ref facts, ref plan);
            if (done) return true;
            (facts, plan.Count) = (before, mark); // backtrack
        }

        return false;
    }

    public static bool Holds(ulong facts, ulong requires, ulong forbids) => (facts & requires) == requires && (facts & forbids) == 0;
}

/// <summary>Builds an <see cref="HtnDomain"/> over the facts of enum <typeparamref name="TFact"/> (flags, at most 64).</summary>
public sealed class DomainBuilder<TFact> where TFact : struct, Enum
{
    private readonly List<(TaskEntry Entry, Action<Entity>? Start, Func<Entity, bool>? Running, List<(ulong, ulong, int[])>? Methods)> _tasks = [];

    /// <summary>A primitive task carried out as component <typeparamref name="TTask"/>.</summary>
    public int Primitive<TTask>(TFact requires = default, TFact forbids = default, TFact sets = default, TFact clears = default)
        where TTask : struct, IComponent
    {
        _tasks.Add((new TaskEntry(Bits(requires), Bits(forbids), Bits(sets), Bits(clears), -1, 0),
            static e => e.Add<TTask>(), static e => e.Has<TTask>(), null));
        return _tasks.Count - 1;
    }

    /// <summary>A compound task; give it methods with <see cref="Method"/>, in order of preference.</summary>
    public int Compound()
    {
        _tasks.Add((default, null, null, []));
        return _tasks.Count - 1;
    }

    public DomainBuilder<TFact> Method(int compound, TFact requires, TFact forbids, params int[] steps)
    {
        _tasks[compound].Methods!.Add((Bits(requires), Bits(forbids), steps));
        return this;
    }

    public DomainBuilder<TFact> Method(int compound, TFact requires, params int[] steps) => Method(compound, requires, default, steps);

    public HtnDomain Build(int root)
    {
        var tasks = new TaskEntry[_tasks.Count];
        var methods = new List<MethodEntry>();
        var steps = new List<int>();
        var start = new Action<Entity>[_tasks.Count];
        var running = new Func<Entity, bool>[_tasks.Count];
        for (int i = 0; i < _tasks.Count; i++)
        {
            var (entry, s, r, ms) = _tasks[i];
            (start[i], running[i]) = (s ?? Never, r ?? NotRunning);
            if (ms == null)
            {
                tasks[i] = entry;
                continue;
            }

            tasks[i] = new TaskEntry(0, 0, 0, 0, methods.Count, ms.Count);
            foreach (var (requires, forbids, of) in ms)
            {
                methods.Add(new MethodEntry(requires, forbids, steps.Count, of.Length));
                steps.AddRange(of);
            }
        }

        return new HtnDomain(root, tasks, [.. methods], [.. steps], start, running);

        static void Never(Entity _) => throw new InvalidOperationException("a compound task never runs");
        static bool NotRunning(Entity _) => false;
    }

    private static ulong Bits(TFact fact) => Convert.ToUInt64(fact);
}

