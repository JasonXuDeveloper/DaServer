using System;
using System.Diagnostics;
using EasyEcs;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>Runs timer callbacks that are due (<see cref="Shard.Schedule"/>).</summary>
[UpdateInGroup<ShardMessages>]
internal sealed class TimerSystem : EcsSystem
{
    public override int Priority => 4;

    protected override void OnUpdate()
    {
        var timers = World.GetSingleton<ShardTimers>().Queue;
        long now = Stopwatch.GetTimestamp();
        while (timers.TryPeek(out var timer, out long due) && due <= now)
        {
            timers.Dequeue();
            try
            {
                timer.Callback(timer.State);
            }
            catch (Exception e)
            {
                World.Logger().LogError(e, "Timer callback failed");
            }
        }
    }
}
