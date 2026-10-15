using DaServer.Protocol;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// Removes retiring activations (<see cref="Retiring"/>): drops them from the directory, destroys their entity and
/// routes their remaining mail again (now to the new owner, or a new activation here) or fails it.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class RetireSystem : EcsSystem
{
    public override int Priority => 3;

    [ForEach]
    private static void Retire(Entity actor, in Retiring retiring, in Mailbox mailbox, in ActorLink link,
        ref ActorDirectory directory, World world)
    {
        var (status, message) = retiring;
        var pending = mailbox.Pending;
        directory.Actors.Remove(link.Id);
        actor.Destroy(); // the refs above are invalid from here on

        while (pending.TryDequeue(out var envelope))
        {
            if (status == RpcStatus.Ok) ShardDispatch.Route(world, envelope.Frame, envelope.Sink);
            else ShardDispatch.Fail(envelope.Frame, envelope.Sink, status, message ?? status.ToString());
        }
    }
}
