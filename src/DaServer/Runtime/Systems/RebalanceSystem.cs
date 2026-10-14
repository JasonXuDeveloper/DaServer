using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// After a membership change (or on shutdown), starts deactivating actors this node no longer owns. Busy actors are
/// retried on the next wake; their mail is forwarded once <see cref="Actor.OnDeactivateAsync"/> completes.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed class RebalanceSystem : EcsSystem
{
    private static readonly QueryFilter Idle = QueryFilter.Create().Without<Turn>().Without<Deactivating>();
    private static readonly QueryFilter Busy = QueryFilter.Create().With<Turn>().Without<Deactivating>();

    public override int Priority => 5;

    protected override void OnUpdate()
    {
        ref var activity = ref World.GetSingleton<ShardActivity>();
        if (!activity.Rebalance) return;
        activity.Rebalance = false;

        bool stopping = activity.Stopping;
        var placement = World.Shard().Node.Placement;
        foreach (var item in World.Query<ActorLink, ActorIdentity>(Idle))
        {
            var id = item.Component2.Id;
            if (!stopping && placement.IsLocalOwner(id.ImplementationId, id.Key)) continue;
            var actor = item.Entity;
            var link = item.Component1.Actor;
            actor.Add<Deactivating>();
            actor.Add<Turn>() = new Turn(ShardDispatch.Start(static a => a.OnDeactivateAsync(), link), TurnKind.Deactivation);
        }

        // Busy actors were skipped: if one of them must move, look again on the next wake (a turn's end wakes the shard).
        foreach (var item in World.Query<ActorIdentity>(Busy))
        {
            var id = item.Component1.Id;
            if (stopping || !placement.IsLocalOwner(id.ImplementationId, id.Key))
            {
                World.GetSingleton<ShardActivity>().Rebalance = true;
                break;
            }
        }
    }
}
