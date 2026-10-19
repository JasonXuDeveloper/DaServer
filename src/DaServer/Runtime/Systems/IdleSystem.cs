namespace DaServer.Runtime;

/// <summary>
/// Asks actors that took no mail for their <see cref="ActorSettings.UnloadAfterIdleSeconds"/> to leave
/// (<see cref="UnloadRequested"/>). Visits only idle live actors that opted in (<see cref="UnloadWhenIdle"/>), a few times a
/// second (<see cref="ShardIdleScan"/>).
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed class IdleSystem : EcsSystem
{
    [ForEach, Without<Ready>, Without<Loading>, Without<UnloadRequested>, Without<Leaving>, Without<Removing>]
    private static void Leave(Entity actor, in LastMail last, in UnloadWhenIdle idle, in ShardActivity activity)
    {
        if (activity.NowMs - last.Ms >= idle.Milliseconds) actor.Set(new UnloadRequested());
    }
}

/// <summary>Removes the replies kept for later whose callers stopped waiting (<see cref="PendingReply"/>): nobody reads them now.</summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed class ReplySlotSystem : EcsSystem
{
    [ForEach]
    private static void Expire(Entity reply, in PendingReply pending)
    {
        if (pending.Deadline != 0 && Platform.Milliseconds > pending.Deadline) reply.Destroy();
    }
}
