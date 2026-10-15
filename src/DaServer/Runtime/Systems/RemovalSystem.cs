namespace DaServer.Runtime;

/// <summary>
/// Removes actors marked <see cref="Removing"/>: drops them from the directory, destroys their entity and
/// routes their remaining mail again (now to the new owner, or loads the actor here again) or fails it.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class RemovalSystem : EcsSystem
{
    public override int Priority => 3;

    [ForEach]
    private static void Remove(Entity actor, in Removing removing, in Mailbox mailbox, in ActorRef self,
        ref ActorDirectory directory, World world)
    {
        var (status, message) = removing;
        var pending = mailbox.Pending;
        directory.Actors.Remove(self.Id);
        actor.Destroy(); // the refs above are invalid from here on

        while (pending.TryDequeue(out var envelope))
        {
            if (status == RpcStatus.Ok) ShardDispatch.Route(world, envelope.Frame, envelope.Sink);
            else ShardDispatch.Fail(envelope.Frame, envelope.Sink, status, message ?? status.ToString());
        }
    }
}
