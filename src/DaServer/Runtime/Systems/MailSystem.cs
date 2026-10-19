namespace DaServer.Runtime;

/// <summary>
/// Runs the mail waiting in live actors' mailboxes (<see cref="Ready"/>), in arrival order, one message after the other:
/// handlers run to completion, so nothing waits between them. Mail for an actor that is live and has none waiting runs
/// at once on arrival (<see cref="ShardDispatch.Deliver"/>); this system drains what waited while the actor loaded.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<UnloadSystem>]
internal sealed class MailSystem : EcsSystem
{
    /// <summary>Upper bound per wake (fairness with timers and the tick under a flood).</summary>
    private const int Budget = 4096;

    /// <summary>Actors <see cref="Run"/> visits (the shard does not sleep while one is left).</summary>
    internal static readonly QueryFilter Runnable = QueryFilter.Create().With<Ready>()
        .Without<Loading>().Without<UnloadRequested>().Without<Leaving>().Without<Removing>();

    protected override void OnUpdate() => World.GetSingleton<ShardActivity>().MailBudget = Budget;

    [ForEach, With<Ready>, Without<Loading>, Without<UnloadRequested>, Without<Leaving>, Without<Removing>]
    private static void Run(Entity actor, in ActorKey key, ref ShardActivity activity, World world)
    {
        if (activity.MailBudget <= 0) return; // continue next wake (the actor stays Ready)
        var address = key.Address;
        int processed = 0;
        // Re-read the mailbox after every message: a handler's structural changes move the actor's row.
        while (processed < activity.MailBudget && processed < actor.Read<DynamicBuffer<Envelope>>().Count)
        {
            var envelope = actor.Read<DynamicBuffer<Envelope>>()[processed];
            processed++;
            ShardDispatch.Invoke(world, actor, address, in envelope);
            if (!actor.IsAlive || !ShardDispatch.IsLive(actor)) break; // reloading after a failure: the rest waits for the new one
        }

        activity.MailBudget -= processed; // singletons never move: the ref survives the handlers' structural changes
        activity.Processed += processed;
        if (!actor.IsAlive) return;
        ref var mailbox = ref actor.Get<DynamicBuffer<Envelope>>();
        var items = mailbox.AsSpan();
        items[processed..].CopyTo(items); // what is left moves to the front, in order
        for (int i = 0; i < processed; i++) mailbox.RemoveAt(mailbox.Count - 1);
        if (mailbox.Count == 0) actor.Remove<Ready>(); // the buffer keeps its room for the actor's next wait
    }
}
