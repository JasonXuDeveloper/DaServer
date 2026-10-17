namespace DaServer.Runtime;

/// <summary>
/// Asks the actors this node no longer owns (all of them when stopping or declared dead) to leave (<see cref="UnloadRequested"/>);
/// busy ones leave when their turn ends. Declared dead, they are also <see cref="Fenced"/>. Runs only after ownership may
/// have changed (<see cref="ShardRebalance"/>).
/// </summary>
[UpdateInGroup<ShardRebalance>]
internal sealed partial class RebalanceSystem : EcsSystem
{
    [ForEach, Without<UnloadRequested>, Without<Unloading>, Without<Removing>]
    private static void Leave(Entity actor, in ActorRef self, in ShardActivity activity, World world)
    {
        if (!activity.DeclaredDead && (activity.Stopping || !world.Shard().Node.IsLocalOwner(self.Actor.HandlerType.Id, self.Actor.Key)))
            actor.Add<UnloadRequested>();
    }

    // Wherever a copy is in leaving (asked to, unloading, its record about to be sent or written), its key has a new
    // owner now: it goes without OnUnload, a write or a handoff. One whose record was sent waits for the answer.
    [ForEach, Without<Fenced>, Without<Removing>]
    private static void Fence(Entity actor, in ActorRef self, in ShardActivity activity)
    {
        if (!activity.DeclaredDead) return;
        actor.Add<Fenced>();
        if (!actor.Has<UnloadRequested>() && !actor.Has<Unloading>()) actor.Add<UnloadRequested>();
    }
}
