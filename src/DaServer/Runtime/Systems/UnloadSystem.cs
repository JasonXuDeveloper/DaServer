namespace DaServer.Runtime;

/// <summary>
/// Starts unloading actors asked to leave (<see cref="UnloadRequested"/>) as soon as they are idle:
/// <see cref="Actor.OnUnload"/> runs as their last turn (<see cref="Unloading"/>), then they are removed (<see cref="Removing"/>).
/// <see cref="Fenced"/> actors are removed at once: their key already has a new owner.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class UnloadSystem : EcsSystem
{
    public override int Priority => 4;

    [ForEach, With<UnloadRequested>, Without<Turn>, Without<Removing>, Without<Fenced>, Without<Loading>, Without<FinalSave>]
    private static void Begin(Entity actor, in ActorRef self, World world)
    {
        var target = self.Actor;
        actor.Remove<UnloadRequested>();
        actor.Add<Unloading>();
        TurnWatcher.Begin(world, actor, ShardDispatch.Hook(world, static a => a.OnUnload().AsValueTask(), target));
    }

    // After its write in flight (Without<Saving>): a reload reads the store only once that write settled. A record sent
    // to a new owner (HandingOff) waits for its answer: taken, the copy goes; refused, it is dropped here.
    [ForEach, With<UnloadRequested>, With<Fenced>, Without<Turn>, Without<Removing>, Without<Saving>, Without<HandingOff>]
    private static void Drop(Entity actor)
    {
        actor.Remove<UnloadRequested, Fenced>();
        if (actor.Has<FinalSave>()) actor.Remove<FinalSave>();
        if (actor.Has<HandOff>()) actor.Remove<HandOff>();
        actor.Add<Removing>() = new Removing(RpcStatus.Ok, null); // mail goes on to the new owner
    }
}
