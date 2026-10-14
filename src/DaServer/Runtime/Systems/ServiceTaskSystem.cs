using System;
using EasyEcs;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>Collects finished asynchronous service calls (errors were already replied; this logs and recycles).</summary>
[UpdateInGroup<ShardMessages>]
internal sealed class ServiceTaskSystem : EcsSystem
{
    public override int Priority => 3;

    protected override void OnUpdate()
    {
        var tasks = World.GetSingleton<ServiceTasks>().Tasks;
        for (int i = tasks.Count - 1; i >= 0; i--)
        {
            var task = tasks[i];
            if (!task.IsCompleted) continue;
            tasks.RemoveAt(i);
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                World.Logger().LogError(e, "Service call failed");
            }
        }
    }
}
