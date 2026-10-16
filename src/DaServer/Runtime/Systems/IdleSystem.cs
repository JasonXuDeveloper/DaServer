namespace DaServer.Runtime;

/// <summary>
/// Asks actors that received no mail for their <see cref="Actor.UnloadAfterIdle"/> to leave (<see cref="UnloadRequested"/>).
/// Visits only idle actors that opted in (<see cref="UnloadWhenIdle"/>), a few times a second (<see cref="ShardIdleScan"/>).
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed partial class IdleSystem : EcsSystem
{
    [ForEach, Without<Ready>, Without<Turn>, Without<UnloadRequested>, Without<Unloading>, Without<Removing>]
    private static void Leave(Entity actor, in Mailbox mailbox, in UnloadWhenIdle idle, in ShardActivity activity)
    {
        if (activity.NowMs - mailbox.LastActive >= idle.Milliseconds) actor.Add<UnloadRequested>();
    }
}
