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

    // A copy still waiting for its record when it is asked to leave holds no state: it goes at once (its mail to the
    // owner; a node waiting for it hears "none"), instead of two copies each waiting for the other's record.
    [ForEach, With<UnloadRequested>, Without<Fenced>, Without<Removing>]
    private static void Abandon(Entity actor, in AwaitingHandoff awaiting)
    {
        // A node that asked this copy is sent to the one this copy waited for (the record comes from there), unless the
        // asker is that node: then it hears "none" (two copies each waiting for the other).
        if (actor.TryGet<HandoffWanted>(out var wanted) && wanted.By != awaiting.From)
            actor.Add<HandedTo>() = new HandedTo(awaiting.From);
        actor.Remove<UnloadRequested, AwaitingHandoff>();
        actor.Add<Removing>() = new Removing(RpcStatus.Ok, null);
    }

    // After its write in flight (Without<Saving>): a reload reads the store only once that write settled; after its
    // OnUnload turn (Without<Turn>). A copy whose record went to a new owner (HandingOff) goes too: this node was
    // declared dead, so the copy writes nothing whatever the answer, and its mail follows the record on the same link.
    [ForEach, With<UnloadRequested>, With<Fenced>, Without<Turn>, Without<Removing>, Without<Saving>]
    private static void Drop(Entity actor)
    {
        actor.Remove<UnloadRequested, Fenced>();
        if (actor.Has<FinalSave>()) actor.Remove<FinalSave>();
        if (actor.Has<HandOff>()) actor.Remove<HandOff>();
        if (actor.Has<HandingOff>()) actor.Remove<HandingOff>();
        actor.Add<Removing>() = new Removing(RpcStatus.Ok, null); // mail goes on to the new owner
    }
}
