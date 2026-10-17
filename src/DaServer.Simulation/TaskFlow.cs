using EasyEcs;

namespace DaServer.Simulation;

/// <summary>
/// A task carried out by an asynchronous flow (log in, change maps, pay): the flow runs on the agent's loop after the
/// current systems (it may change the world), and the task's component <typeparamref name="TTask"/> is removed when it
/// ends, so the planner moves on. A flow that fails or returns false counts in <paramref name="failures"/>.
/// </summary>
public static class TaskFlow
{
    public static void Start<TTask>(Entity agent, Func<Task<bool>> flow, SwarmCounter? failures = null) where TTask : struct, IComponent =>
        (SynchronizationContext.Current ?? throw new InvalidOperationException("Start flows on a client loop"))
        .Post(static async s =>
        {
            var (agent, flow, failures) = ((Entity, Func<Task<bool>>, SwarmCounter?))s!;
            try
            {
                if (!await flow()) failures?.Add();
            }
            catch (Exception e)
            {
                failures?.Add(e);
            }
            finally
            {
                if (agent.IsAlive && agent.Has<TTask>()) agent.Remove<TTask>();
            }
        }, (agent, flow, failures));
}
