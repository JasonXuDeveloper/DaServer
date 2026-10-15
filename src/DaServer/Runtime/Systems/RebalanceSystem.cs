using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// After a membership change (or on shutdown), asks the actors this node no longer owns to leave
/// (<see cref="Unloading"/>); busy ones leave when their turn ends. Runs only when asked
/// (<see cref="ShardActivity.Rebalance"/>).
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed class RebalanceSystem : EcsSystem
{
    private static readonly QueryFilter Staying = QueryFilter.Create().Without<Unloading>().Without<Deactivating>()
        .Without<Retiring>();

    public override int Priority => 5;

    protected override void OnUpdate()
    {
        ref var activity = ref World.GetSingleton<ShardActivity>();
        if (!activity.Rebalance) return;
        bool all = activity.Stopping || activity.RetireAll;
        activity.Rebalance = activity.RetireAll = false;

        var placement = World.Shard().Node.Placement;
        foreach (var item in World.Query<ActorIdentity>(Staying))
        {
            var id = item.Component1.Id;
            if (all || !placement.IsLocalOwner(id.ImplementationId, id.Key)) item.Entity.Add<Unloading>();
        }
    }
}
