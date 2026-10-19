namespace DaServer.Runtime;

/// <summary>
/// Ends actor turns whose task completed (<see cref="TurnEnded"/>): a call frees the actor for its next message, a
/// load opens the mailbox (or removes the actor when it failed), an unload removes it. Only ended turns are visited.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<DispatchSystem>]
internal sealed partial class TurnSystem : EcsSystem
{

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
        target.TurnChain = 0;
        actor.Remove<Turn, TurnEnded, Loading>();
        bool restored = actor.TryGet<RestoredRecord>(out var record);
        if (restored) actor.Remove<RestoredRecord>();
        if (error == null) return;
        Log.ActorFailed(world.Logger, error, "Load", target);
        if (restored && actor.Has<Stored>())
        {
            // Restored from a handoff its sender let go: the record is the only copy of the moved state, so it is written
            // as it came (not as the failed OnLoad left it) before the copy goes, and the mail loads it from the store.
            Persistence.Handoff.RestoreAsReceived(world, actor, record.Payload);
            actor.Set(new Unloading());
            actor.Set(new FinalSave());
            return;
        }

        actor.Set<Removing>(new Removing(RpcStatus.Error, "Load failed: " + error.Message));
    }

    [ForEach, With<TurnEnded>, With<Unloading>]
    private static void EndUnload(Entity actor, in Turn turn, in ActorRef self, World world)
    {
        var error = TurnWatcher.Result(in turn);
        var target = self.Actor;
        target.TurnChain = 0;
        if (error != null) Log.ActorFailed(world.Logger, error, "Unload", target);
        actor.Remove<Turn, TurnEnded>();
        if (actor.Has<Fenced>()) // its write was refused meanwhile: the key has a newer owner, this copy goes as it is
        {
            actor.Remove<UnloadRequested, Fenced>();
            actor.Set<Removing>(new Removing(RpcStatus.Ok, null));
        }
        else if (Handoff.TargetOf(world, target) is { } to) actor.Set<HandOff>(new HandOff(to)); // its new owner takes the record
        else if (actor.Has<Stored>()) actor.Set(new FinalSave()); // removed once its record is written (PersistSystem)
        else actor.Set<Removing>(new Removing(RpcStatus.Ok, null));
    }
}
