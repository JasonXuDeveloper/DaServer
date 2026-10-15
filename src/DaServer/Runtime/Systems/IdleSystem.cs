using System;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// Unloads actors that received no mail for their <see cref="Actor.UnloadAfterIdle"/>. Visits only actors that opted
/// in (<see cref="UnloadWhenIdle"/>), and only a few times a second.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed class IdleSystem : EcsSystem
{
    private const long ScanEveryMs = 100;
    private static readonly QueryFilter Idle = QueryFilter.Create().Without<Turn>().Without<Deactivating>();

    public override int Priority => 6;

    protected override void OnUpdate()
    {
        long now = Environment.TickCount64;
        ref var activity = ref World.GetSingleton<ShardActivity>();
        if (now < activity.NextIdleScan) return;
        activity.NextIdleScan = now + ScanEveryMs;

        foreach (var item in World.Query<ActorLink, Mailbox, UnloadWhenIdle>(Idle))
        {
            ref var mailbox = ref item.Component2;
            if (mailbox.Pending.Count == 0 && now - mailbox.LastActive >= item.Component3.Milliseconds)
                ShardDispatch.Unload(item.Entity, item.Component1.Actor);
        }
    }
}
