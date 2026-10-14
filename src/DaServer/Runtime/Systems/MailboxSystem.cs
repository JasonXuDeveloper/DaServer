using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// Runs the mail of ready actors, one turn at a time per actor: synchronous handlers run back to back; a handler that
/// awaits becomes the actor's <see cref="Turn"/> and its next message waits for it. Only ready actors are visited.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed class MailboxSystem : EcsSystem
{
    /// <summary>Upper bound per wake (fairness with timers and the tick under a flood).</summary>
    private const int Budget = 4096;

    public override int Priority => 2;

    protected override void OnUpdate()
    {
        var world = World;
        var ready = world.GetSingleton<ReadyActors>().Queue;
        int processed = 0;
        while (processed < Budget && ready.TryDequeue(out var actor))
        {
            if (!actor.IsAlive) continue;
            ref var mailbox = ref actor.Get<Mailbox>();
            mailbox.Scheduled = false;
            var pending = mailbox.Pending;  // the queue object outlives refs invalidated by handlers' structural changes
            var target = actor.Get<ActorLink>().Actor;

            while (processed < Budget && pending.TryDequeue(out var envelope))
            {
                processed++;
                var task = ShardDispatch.Invoke(target, in envelope);
                if (task.IsCompleted) continue;
                actor.Add<Turn>() = new Turn(task, TurnKind.Call);
                break;
            }

            ShardDispatch.Schedule(world, actor, ref actor.Get<Mailbox>()); // budget exhausted: continue next wake
        }

        world.GetSingleton<ShardActivity>().Processed += processed;
    }
}
