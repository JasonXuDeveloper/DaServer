using System;
using DaServer.Protocol;
using EasyEcs;
using Microsoft.Extensions.Logging;

namespace DaServer.Runtime;

/// <summary>
/// Ends finished actor turns: a call lets the actor take its next message, an activation opens the mailbox (or fails
/// it), a deactivation retires the entity and forwards the remaining mail to the new owner. Only busy actors match.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class TurnSystem : EcsSystem
{
    public override int Priority => 1;

    [ForEach]
    private static void Finish(Entity actor, in Turn turn, World world)
    {
        if (!turn.Task.IsCompleted) return;

        var kind = turn.Kind;
        Exception? error = null;
        try
        {
            turn.Task.GetAwaiter().GetResult(); // also returns pooled state machines to their pools
        }
        catch (Exception e)
        {
            error = e;
        }

        actor.Remove<Turn>(); // `turn` is invalid from here on
        if (error != null)
            world.Logger().LogError(error, "{Kind} of {Actor} failed", kind, actor.Get<ActorLink>().Actor);

        switch (kind)
        {
            case TurnKind.Activation when error != null:
                ShardDispatch.Retire(world, actor, RpcStatus.Error, "Activation failed: " + error.Message);
                return;
            case TurnKind.Deactivation:
                ShardDispatch.Retire(world, actor);
                return;
            default:
                ShardDispatch.Schedule(world, actor, ref actor.Get<Mailbox>());
                return;
        }
    }
}
