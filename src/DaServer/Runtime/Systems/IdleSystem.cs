using System;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// Asks actors that received no mail for their <see cref="Actor.UnloadAfterIdle"/> to leave (<see cref="Unloading"/>).
/// Visits only idle actors that opted in (<see cref="UnloadWhenIdle"/>), a few times a second (<see cref="ShardIdleScan"/>).
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed partial class IdleSystem : EcsSystem
{
    [ForEach, Without<Ready>, Without<Turn>, Without<Unloading>, Without<Deactivating>, Without<Retiring>]
    private static void Leave(Entity actor, in Mailbox mailbox, in UnloadWhenIdle idle)
    {
        if (Environment.TickCount64 - mailbox.LastActive >= idle.Milliseconds) actor.Add<Unloading>();
    }
}
