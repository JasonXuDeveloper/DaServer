namespace DaServer.Runtime;

/// <summary>
/// Asks the actors this node no longer owns (all of them when stopping or declared dead) to leave (<see cref="UnloadRequested"/>);
/// loading ones once their record arrived. Declared dead, they are also <see cref="Fenced"/>. Runs only after ownership may
/// have changed (<see cref="ShardRebalance"/>).
/// </summary>
[UpdateInGroup<ShardRebalance>]
internal sealed class RebalanceSystem : EcsSystem
{
    [ForEach, Without<UnloadRequested>, Without<Leaving>, Without<Removing>]
    private static void Leave(Entity actor, in ActorKey key, in ShardActivity activity, World world)
    {
        if (!activity.DeclaredDead && (activity.Stopping || !world.Shard.Node.IsLocalOwner(key.HandlerTypeId, key.Key)))
            actor.SetUnloadRequested();
    }

    // Wherever a copy is in leaving (asked to, unloading, its record about to be sent or written), its key has a new
    // owner now: it goes without its Unloading mail, a write or a handoff. One whose record was sent waits for the answer.
    [ForEach, With<ActorKey>, Without<Fenced>, Without<Removing>]
    private static void Fence(Entity actor, in ShardActivity activity)
    {
        if (!activity.DeclaredDead) return;
        actor.SetFenced();
        // Also a copy past its Unloading mail (its record waiting to be written or sent): UnloadSystem.Drop removes it.
        actor.SetUnloadRequested();
    }
}
