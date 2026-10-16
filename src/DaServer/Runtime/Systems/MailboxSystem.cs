namespace DaServer.Runtime;

/// <summary>
/// Runs the mail of ready actors, one turn at a time per actor: synchronous handlers run back to back; a handler that
/// awaits becomes the actor's <see cref="Turn"/> and its next message waits for it. Only actors with mail and no turn
/// are visited (<see cref="Ready"/>).
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class MailboxSystem : EcsSystem
{
    /// <summary>Upper bound per wake (fairness with timers and the tick under a flood).</summary>
    private const int Budget = 4096;

    /// <summary>Actors <see cref="Run"/> visits (the shard does not sleep while one is left).</summary>
    internal static readonly QueryFilter Runnable = QueryFilter.Create().With<Ready>().Without<Turn>()
        .Without<UnloadRequested>().Without<Removing>();

    public override int Priority => 5;

    protected override void OnUpdate() => World.GetSingleton<ShardActivity>().MailBudget = Budget;

    [ForEach, With<Ready>, Without<Turn>, Without<UnloadRequested>, Without<Removing>]
    private static void Run(Entity actor, ref Mailbox mailbox, in ActorRef self, ref ShardActivity activity, World world)
    {
        if (activity.MailBudget <= 0) return; // continue next wake (the actor stays Ready)
        mailbox.LastActive = Environment.TickCount64;
        var pending = mailbox.Pending; // the queue outlives refs invalidated by handlers' structural changes
        var target = self.Actor;
        int processed = 0;
        while (processed < activity.MailBudget && pending.TryDequeue(out var envelope))
        {
            processed++;
            var task = ShardDispatch.Invoke(target, in envelope);
            if (task.IsCompleted)
            {
                target.TurnChain = 0;
                task.GetAwaiter().GetResult(); // completed while being dispatched: release a pooled source (errors were replied)
                if (actor.Has<UnloadRequested>()) break; // its handler failed and it reloads: the rest waits for the new one
                continue;
            }
            TurnWatcher.Begin(world, actor, task);
            break;
        }

        activity.MailBudget -= processed; // singletons never move: the ref survives the handlers' structural changes
        activity.Processed += processed;
        if (pending.Count == 0) actor.Remove<Ready>();
    }
}
