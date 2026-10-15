namespace DaServer.Runtime;

/// <summary>
/// Starts unloading actors asked to leave (<see cref="Unloading"/>) as soon as they are idle:
/// <see cref="Actor.OnUnload"/> runs as their last turn (<see cref="Deactivating"/>), then they retire.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class UnloadSystem : EcsSystem
{
    public override int Priority => 4;

    [ForEach, With<Unloading>, Without<Turn>, Without<Retiring>]
    private static void Begin(Entity actor, in ActorLink link, World world)
    {
        var target = link.Actor;
        actor.Remove<Unloading>();
        actor.Add<Deactivating>();
        TurnWatcher.Begin(world, actor, ShardDispatch.Start(static a => a.OnUnload(), target));
    }
}
