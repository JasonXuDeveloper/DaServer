using System;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// Asks actors that received no mail for their <see cref="Actor.UnloadAfterIdle"/> to leave (<see cref="Unloading"/>).
/// Visits only idle actors that opted in (<see cref="UnloadWhenIdle"/>), and only a few times a second.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed class IdleSystem : EcsSystem
{
    private const long ScanEveryMs = 100;

    private static readonly QueryFilter Idle = QueryFilter.Create().Without<Ready>().Without<Turn>()
        .Without<Unloading>().Without<Deactivating>().Without<Retiring>();

    public override int Priority => 6;

    protected override void OnUpdate()
    {
        long now = Environment.TickCount64;
        ref var activity = ref World.GetSingleton<ShardActivity>();
        if (now < activity.NextIdleScan) return;
        activity.NextIdleScan = now + ScanEveryMs;

        foreach (var item in World.Query<Mailbox, UnloadWhenIdle>(Idle))
            if (now - item.Component1.LastActive >= item.Component2.Milliseconds)
                item.Entity.Add<Unloading>();
    }
}
