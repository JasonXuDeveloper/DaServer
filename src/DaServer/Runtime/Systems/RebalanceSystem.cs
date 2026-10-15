namespace DaServer.Runtime;

/// <summary>
/// Asks the actors this node no longer owns (all of them when stopping or declared dead) to leave (<see cref="UnloadRequested"/>);
/// busy ones leave when their turn ends. Runs only after ownership may have changed (<see cref="ShardRebalance"/>).
/// </summary>
[UpdateInGroup<ShardRebalance>]
internal sealed partial class RebalanceSystem : EcsSystem
{
    [ForEach, Without<UnloadRequested>, Without<Unloading>, Without<Removing>]
    private static void Leave(Entity actor, in ActorRef self, in ShardActivity activity, World world)
    {
        if (activity.UnloadAll || !world.Shard().Node.IsLocalOwner(self.Actor.HandlerType.Id, self.Key))
            actor.Add<UnloadRequested>();
    }
}
