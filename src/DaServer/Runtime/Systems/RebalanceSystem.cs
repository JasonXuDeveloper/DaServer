namespace DaServer.Runtime;

/// <summary>
/// Asks the actors this node no longer owns (all of them when stopping or retired) to leave (<see cref="Unloading"/>);
/// busy ones leave when their turn ends. Runs only after ownership may have changed (<see cref="ShardRebalance"/>).
/// </summary>
[UpdateInGroup<ShardRebalance>]
internal sealed partial class RebalanceSystem : EcsSystem
{
    [ForEach, Without<Unloading>, Without<Deactivating>, Without<Retiring>]
    private static void Leave(Entity actor, in ActorLink link, in ShardActivity activity, World world)
    {
        if (activity.LeaveAll || !world.Shard().Node.Placement.IsLocalOwner(link.Actor.Implementation.Id, link.Key))
            actor.Add<Unloading>();
    }
}
