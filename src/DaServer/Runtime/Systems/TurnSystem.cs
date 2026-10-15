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
        actor.Remove<Turn, TurnEnded>(); // `turn` is invalid from here on
        if (error != null) world.Logger().LogError(error, "Call of {Actor} failed", self.Actor);
    }

    [ForEach, With<TurnEnded>, With<Loading>]
    private static void EndLoad(Entity actor, in Turn turn, in ActorRef self, World world)
    {
        var error = TurnWatcher.Result(in turn);
        var target = self.Actor;
        actor.Remove<Turn, TurnEnded, Loading>();
        if (error == null) return;
        world.Logger().LogError(error, "Load of {Actor} failed", target);
        actor.Add<Removing>() = new Removing(RpcStatus.Error, "Load failed: " + error.Message);
    }

    [ForEach, With<TurnEnded>, With<Unloading>]
    private static void EndUnload(Entity actor, in Turn turn, in ActorRef self, World world)
    {
        var error = TurnWatcher.Result(in turn);
        if (error != null) world.Logger().LogError(error, "Unload of {Actor} failed", self.Actor);
        actor.Remove<Turn, TurnEnded>();
        actor.Add<Removing>() = new Removing(RpcStatus.Ok, null);
    }
}
