namespace DaServer.Runtime;

/// <summary>
/// Removes actors marked <see cref="Removing"/>: drops them from the directory, destroys their entity and
/// routes their remaining mail again (now to the new owner, or loads the actor here again) or fails it. What the actor
/// held returns to the shard's <see cref="ActorPools"/>.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<ServiceCallSystem>]
[UpdateAfter<LoadSystem>]
internal sealed partial class RemovalSystem : EcsSystem
{

    [ForEach]
    private static void Remove(Entity actor, in Removing removing, in Mailbox mailbox, in ActorRef self,
        ref ActorDirectory directory, World world)
    {
        var (status, message) = removing;
        var pending = mailbox.Pending;
        var target = self.Actor;
        bool hasSessions = actor.TryGet<ClientSessions>(out var sessions);
        // A node that asked for the record: "none" after any record it got, or where the record went (asking it there
        // instead of loading a second copy from the store beside the one running there).
        if (actor.TryGet<HandoffWanted>(out var wanted))
        {
            if (actor.TryGet<HandedTo>(out var handed) && handed.To != wanted.By) Handoff.Moved(world, target, wanted.By, handed.To);
            else Handoff.Gone(world, target, wanted.By);
        }
        directory.Actors.Remove(self.Address);
        actor.Destroy(); // the refs above are invalid from here on

        while (pending.TryDequeue(out var envelope))
        {
            if (status == RpcStatus.Ok) ShardDispatch.Route(world, envelope.Frame, envelope.Sink); // may load the key again
            else ShardDispatch.Fail(envelope.Frame, envelope.Sink, status, message);
        }

        world.GetSingleton<ActorPools>().Return(target, pending, in sessions, hasSessions);
    }
}
