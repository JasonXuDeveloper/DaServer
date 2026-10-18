namespace DaServer.Runtime;

/// <summary>
/// Ends actor turns whose task completed (<see cref="TurnEnded"/>): a call frees the actor for its next message, a
/// load opens the mailbox (or removes the actor when it failed), an unload removes it. Only ended turns are visited.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class TurnSystem : EcsSystem
{
    public override int Priority => 1;

    [ForEach, With<TurnEnded>, Without<Loading>, Without<Unloading>]
    private static void EndCall(Entity actor, in Turn turn, in ActorRef self, World world)
    {
        var error = TurnWatcher.Result(in turn);
        var target = self.Actor;
        target.TurnChain = 0;
        actor.Remove<Turn, TurnEnded>(); // `turn` is invalid from here on
        if (error == null) return;
        Log.ActorFailed(world.Logger, error, "Call", target);
        ShardDispatch.Failed(target, error);
    }

    [ForEach, With<TurnEnded>, With<Loading>]
    private static void EndLoad(Entity actor, in Turn turn, in ActorRef self, World world)
    {
        var error = TurnWatcher.Result(in turn);
        var target = self.Actor;
        actor.Remove<Turn, TurnEnded, Loading>();
        if (error == null) return;
        Log.ActorFailed(world.Logger, error, "Load", target);
        actor.Add<Removing>() = new Removing(RpcStatus.Error, "Load failed: " + error.Message);
    }

    [ForEach, With<TurnEnded>, With<Unloading>]
    private static void EndUnload(Entity actor, in Turn turn, in ActorRef self, World world)
    {
        var error = TurnWatcher.Result(in turn);
        var target = self.Actor;
        if (error != null) Log.ActorFailed(world.Logger, error, "Unload", target);
        actor.Remove<Turn, TurnEnded>();
        if (actor.Has<Fenced>()) // its write was refused meanwhile: the key has a newer owner, this copy goes as it is
        {
            actor.Remove<UnloadRequested, Fenced>();
            actor.Add<Removing>() = new Removing(RpcStatus.Ok, null);
        }
        else if (Handoff.TargetOf(world, target) is { } to) actor.Add<HandOff>() = new HandOff(to); // its new owner takes the record
        else if (actor.Has<Stored>()) actor.Add<FinalSave>(); // removed once its record is written (PersistSystem)
        else actor.Add<Removing>() = new Removing(RpcStatus.Ok, null);
    }
}
