namespace DaServer.Runtime;

/// <summary>
/// Starts unloading actors asked to leave (<see cref="UnloadRequested"/>) as soon as they are idle:
/// <see cref="Actor.OnUnload"/> runs as their last turn (<see cref="Unloading"/>), then they are removed (<see cref="Removing"/>).
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class UnloadSystem : EcsSystem
{
    public override int Priority => 4;

    [ForEach, With<UnloadRequested>, Without<Turn>, Without<Removing>]
    private static void Begin(Entity actor, in ActorRef self, World world)
    {
        var target = self.Actor;
        actor.Remove<UnloadRequested>();
        actor.Add<Unloading>();
        TurnWatcher.Begin(world, actor, ShardDispatch.Start(static a => a.OnUnload(), target));
    }
}
