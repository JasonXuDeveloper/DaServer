namespace DaServer.Persistence;

// The handoff systems (ADR-072, Handoff.cs): sending records, waiting for answers, answering deferred requests.

/// <summary>Sends the records of actors leaving for another node (<see cref="HandOff"/>), once no write of theirs is in flight.</summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<MailSystem>]
internal sealed class HandoffSystem : EcsSystem
{
    // After its write in flight and a critical save asked for (the record carries the version they produce).
    [ForEach, Without<Saving>, Without<SaveNow>, Without<Fenced>]
    private static void Send(Entity actor, in HandOff handOff, in ActorKey key, in ShardActivity activity, World world)
    {
        var (to, target, now) = (handOff.To, key.Address, activity.NowMs);
        actor.Remove<HandOff>(); // the refs above are invalid from here on
        if (Handoff.Send(world, actor, target, to) is { } record)
            actor.Set(new HandingOff(to, now + Handoff.TimeoutMs(world.Shard.Node), record));
        else Handoff.Fallback(actor);
    }
}

/// <summary>
/// Answers the deferred requests for records (<see cref="HandoffAsks"/>) once ownership may have changed: "none" for a
/// key this node need not wait for any more, the record (when the actor leaves) for one it loaded meanwhile; the others
/// wait on.
/// </summary>
[UpdateInGroup<ShardRebalance>]
internal sealed class HandoffAsksSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        var waiting = World.GetSingleton<HandoffAsks>().Waiting;
        if (waiting.Count == 0) return;
        var node = World.Shard.Node;
        long now = World.GetSingleton<ShardActivity>().NowMs;
        for (int i = waiting.Count - 1; i >= 0; i--)
        {
            var ask = waiting[i];
            if (now > ask.UntilMs) { } // its asker asked again meanwhile, or will
            else if (ShardDispatch.TryGetLoaded(World, new ActorAddress(ask.HandlerTypeId, ask.Key), out var actor))
            {
                if (!actor.Has<HandoffWanted>()) actor.Set(new HandoffWanted(ask.By));
            }
            else if (Handoff.MustWait(World, ask.HandlerTypeId, ask.Key, ask.By) != null) continue;
            else Handoff.Gone(node, ask.HandlerTypeId, ask.Key, ask.By);

            waiting.SwapRemoveAt(i);
        }
    }
}

/// <summary>
/// Ends handoffs whose other side died before answering: the store takes over (<see cref="ShardIdleScan"/>, a few times
/// a second). While the other side is alive the wait goes on: its answer (or record) comes.
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed class HandoffTimeoutSystem : EcsSystem
{
    [ForEach]
    private static void Waited(Entity actor, in AwaitingHandoff awaiting, in ActorKey key, in ShardActivity activity, World world)
    {
        if (activity.NowMs < awaiting.UntilMs) return;
        var (from, target, now) = (awaiting.From, key.Address, activity.NowMs);
        var node = world.Shard.Node;
        // The previous owner may still run the actor (a save, a slow unload): while it is alive the record
        // will come (or "none"), so ask again instead of loading a second copy from the store beside it.
        if (node.View.TryGetMember(from, out var member) && member.Status != MemberStatus.Dead && Handoff.AskAgain(node, from, target))
        {
            actor.Get<AwaitingHandoff>() = new AwaitingHandoff(from, now + Handoff.TimeoutMs(node));
            return;
        }

        actor.Remove<AwaitingHandoff>();
        ShardDispatch.BeginLoad(world, actor, target);
    }

    [ForEach]
    private static void Unanswered(Entity actor, in HandingOff handingOff, in ActorKey key, in ShardActivity activity, World world)
    {
        if (activity.NowMs < handingOff.UntilMs) return;
        var node = world.Shard.Node;
        // The new owner may have taken the record and be running the actor while its answer is late (a busy node, a
        // link resuming): writing this copy now would fence that one. While it is alive the record goes again (a link
        // that restarted its stream lost the record or the answer): it takes it, or answers "accepted" again for the one
        // it took; only a dead one never answers.
        if (node.View.TryGetMember(handingOff.To, out var member) && member.Status != MemberStatus.Dead &&
            Handoff.Resend(node, key.Address, handingOff.To, handingOff.Record))
        {
            actor.Get<HandingOff>() = handingOff with { UntilMs = activity.NowMs + Handoff.TimeoutMs(node) };
            return;
        }

        var record = handingOff.Record;
        actor.Remove<HandingOff>(); // `handingOff` is invalid from here on
        Handoff.Reclaim(world, actor, record);
    }
}
