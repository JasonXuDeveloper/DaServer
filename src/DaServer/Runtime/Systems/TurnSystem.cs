namespace DaServer.Runtime;

/// <summary>
/// Ends actor turns whose task completed (<see cref="TurnEnded"/>): a call frees the actor for its next message, a
/// load opens the mailbox (or retires the actor when it failed), an unload retires it. Only ended turns are visited.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class TurnSystem : EcsSystem
{
    public override int Priority => 1;

    [ForEach, With<TurnEnded>, Without<Loading>, Without<Deactivating>]
    private static void EndCall(Entity actor, in Turn turn, in ActorLink link, World world)
    {
        var error = TurnWatcher.Result(in turn);
        actor.Remove<Turn, TurnEnded>(); // `turn` is invalid from here on
        if (error != null) world.Logger().LogError(error, "Call of {Actor} failed", link.Actor);
    }

    [ForEach, With<TurnEnded>, With<Loading>]
    private static void EndLoad(Entity actor, in Turn turn, in ActorLink link, World world)
    {
        var error = TurnWatcher.Result(in turn);
        var target = link.Actor;
        actor.Remove<Turn, TurnEnded, Loading>();
        if (error == null) return;
        world.Logger().LogError(error, "Activation of {Actor} failed", target);
        actor.Add<Retiring>() = new Retiring(RpcStatus.Error, "Activation failed: " + error.Message);
    }

    [ForEach, With<TurnEnded>, With<Deactivating>]
    private static void EndUnload(Entity actor, in Turn turn, in ActorLink link, World world)
    {
        var error = TurnWatcher.Result(in turn);
        if (error != null) world.Logger().LogError(error, "Deactivation of {Actor} failed", link.Actor);
        actor.Remove<Turn, TurnEnded>();
        actor.Add<Retiring>() = new Retiring(RpcStatus.Ok, null);
    }
}
