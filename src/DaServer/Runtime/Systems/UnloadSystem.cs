namespace DaServer.Runtime;

/// <summary>
/// Unloads actors asked to leave (<see cref="UnloadRequested"/>): their <see cref="Unloading"/> mail runs, then their
/// record is handed to the key's new owner, written, or they are removed (<see cref="Removing"/>). <see cref="Fenced"/>
/// actors are removed at once: their key already has a new owner.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<RemovalSystem>]
internal sealed class UnloadSystem : EcsSystem
{
    [ForEach, With<UnloadRequested>, Without<Removing>, Without<Fenced>, Without<Loading>, Without<FinalSave>, Without<Leaving>]
    private static void Begin(Entity actor, in ActorKey key, World world)
    {
        var address = key.Address;
        actor.RemoveUnloadRequested(); // `key` is invalid from here on
        actor.SetLeaving();
        ShardDispatch.Unload(world, actor, address);
        if (!actor.IsAlive) return;
        if (actor.IsFenced) // its handler failed and fenced it, or its write was refused: the key has a newer owner
        {
            actor.Remove<UnloadRequested, Fenced>();
            actor.SetRemoving(RpcStatus.Ok);
        }
        else if (Handoff.TargetOf(world, address) is { } to) actor.SetHandOff(to); // its new owner takes the record
        else if (actor.HasStored) actor.SetFinalSave(); // removed once its record is written (PersistSystem)
        else actor.SetRemoving(RpcStatus.Ok);
    }

    // A copy still waiting for its record when it is asked to leave holds no state: it goes at once (its mail to the
    // owner; a node waiting for it hears "none"), instead of two copies each waiting for the other's record.
    [ForEach, With<UnloadRequested>, Without<Fenced>, Without<Removing>]
    private static void Abandon(Entity actor, in AwaitingHandoff awaiting, Maybe<HandoffWanted> wanted)
    {
        // A node that asked this copy is sent to the one this copy waited for (the record comes from there), unless the
        // asker is that node: then it hears "none" (two copies each waiting for the other).
        if (wanted.HasValue && wanted.Value.By != awaiting.From)
            actor.SetHandedTo(awaiting.From);
        actor.Remove<UnloadRequested, AwaitingHandoff>();
        actor.SetRemoving(RpcStatus.Ok);
    }

    // After its write in flight (Without<Saving>): a reload reads the store only once that write settled. A copy whose
    // record went to a new owner (HandingOff) goes too: this node was declared dead, so the copy writes nothing whatever
    // the answer, and its mail follows the record on the same link.
    [ForEach, With<UnloadRequested>, With<Fenced>, Without<Removing>, Without<Saving>]
    private static void Drop(Entity actor)
    {
        actor.Remove<UnloadRequested, Fenced>();
        actor.Remove<FinalSave, HandOff, HandingOff>();
        actor.SetRemoving(RpcStatus.Ok); // mail goes on to the new owner
    }
}
