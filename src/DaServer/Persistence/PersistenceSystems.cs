namespace DaServer.Persistence;

/// <summary>
/// Collects the actors waiting for their record (<see cref="AwaitingRecord"/>) into the shard's load batch, and applies
/// the batch when the store answered: components restored, then <see cref="Actor.OnLoad"/> starts as the first turn.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<TurnSystem>]
internal sealed partial class LoadSystem : EcsSystem
{

    [ForEach]
    private static void Collect(Entity actor, in AwaitingRecord awaiting, in ActorRef self, ref ShardStore store, in ShardActivity activity)
    {
        if (store.Loads.InFlight || store.Loads.Full || activity.NowMs < awaiting.NotBeforeMs) return; // a later batch takes it
        var (target, since) = (self.Actor, awaiting.SinceMs);
        actor.Remove<AwaitingRecord>(); // `awaiting` is invalid from here on
        actor.Set<RecordRequested>(new RecordRequested(since));
        store.Loads.Add(actor, new StoredKey(target.HandlerType.Id, target.Key));
    }

    /// <summary>A failed load is tried again this long after (a store restarting, a failover).</summary>
    private const long RetryLoadMs = 200;

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, LoadBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) Log.LoadFailed(world.Logger, error, batch.Count);
        var store = world.GetSingleton<ShardStore>();
        var records = batch.Records;
        var entities = batch.Entities;
        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!entity.IsAlive || !entity.TryGet<RecordRequested>(out var requested)) continue; // removed meanwhile (fenced)
            entity.Remove<RecordRequested>();
            var actor = entity.Get<ActorRef>().Actor;
            long now = world.GetSingleton<ShardActivity>().NowMs;
            if (error != null && now - requested.SinceMs < (long)world.Shard.Node.Options.CallTimeout.TotalMilliseconds)
            {
                // A store outage delays the actor (its mail waits) instead of failing it: tried again shortly.
                entity.Set<AwaitingRecord>(new AwaitingRecord(requested.SinceMs, now + RetryLoadMs));
                continue;
            }

            try
            {
                if (error != null) throw error;
                Records.Read(entity, records[i].Data.Span, store.ById);
                actor.InitializeEntity(entity); // a state the record does not hold starts new (hashed as it will be written)
                // The hash of what a save would write now: an actor whose record does not change is not written again.
                var fresh = Records.Write(entity, store.Types);
                ulong hash = Records.Hash(fresh.Payload);
                fresh.Dispose();
                long every = (long)actor.SaveEvery.TotalMilliseconds;
                entity.Set<Stored>(new Stored
                {
                    Version = records[i].Version, Hash = hash, EveryMs = every, DueMs = world.GetSingleton<ShardActivity>().NowMs + every,
                });
            }
            catch (Exception e)
            {
                if (e != error) Log.RestoreFailed(world.Logger, e, actor); // the batch's error is logged once
                entity.Set<Removing>(new Removing(RpcStatus.Error, "Loading the actor's record failed: " + e.Message));
                continue;
            }

            ShardDispatch.BeginLive(world, entity, actor);
        }

        batch.Clear();
    }
}

/// <summary>
/// Writes the records that must go now: <see cref="SaveNow"/> (a critical change) and <see cref="FinalSave"/> (an
/// unloaded actor, removed once its record is written). Applies the store's answers: a rejected write means another
/// node owns the actor now, so it is removed here without saving again.
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<MailboxSystem>]
internal sealed partial class PersistSystem : EcsSystem
{

    internal enum Outcome { Queued, Unchanged, Busy, Unwritable }

    // Records are taken between turns only (Without<Turn>): a turn suspended at an await may have changed half of what
    // its message changes, and the record is always the state after whole messages.
    // A fenced or removed actor writes nothing: its state is half-changed (a failed turn) or its key has a new owner.
    [ForEach, With<SaveNow>, Without<Saving>, Without<Turn>, Without<Fenced>, Without<Removing>]
    private static void Now(Entity actor, ref Stored stored, in ActorRef self, in ShardStore store, in ShardActivity activity)
    {
        if (Queue(actor, ref stored, self.Actor, in store, activity.NowMs) != Outcome.Busy) actor.Remove<SaveNow>();
    }

    [ForEach, With<FinalSave>, Without<Saving>, Without<Fenced>]
    private static void Final(Entity actor, ref Stored stored, in ActorRef self, in ShardStore store, in ShardActivity activity)
    {
        if (Queue(actor, ref stored, self.Actor, in store, activity.NowMs) != Outcome.Unchanged) return;
        actor.Remove<FinalSave>();
        actor.Set<Removing>(new Removing(RpcStatus.Ok, null)); // nothing changed since the last write
    }

    /// <summary>
    /// Serialize the actor's record into the shard's save batch unless it equals what was last written. The ref
    /// <paramref name="stored"/> is invalid afterwards (the actor gains <see cref="Saving"/>).
    /// </summary>
    internal static Outcome Queue(Entity actor, ref Stored stored, Actor target, in ShardStore store, long now)
    {
        if (now < stored.RetryAtMs || store.Saves.InFlight || store.Saves.Full) return Outcome.Busy;
        if (stored.EveryMs > 0) stored.DueMs = now + stored.EveryMs;
        FrameWriter record;
        try
        {
            record = Records.Write(actor, store.Types);
        }
        catch (Exception e)
        {
            // The state does not serialize: the store keeps the last record written. One actor's bad state must not
            // hold the shard's other writes and handoffs, so this write is given up; a copy that was leaving goes.
            if (target.Shard.ErrorLogDue(out int unlogged)) Log.RecordUnwritable(target.Shard.Node.Logger, e, target, unlogged);
            if (actor.Has<FinalSave>())
            {
                actor.Remove<FinalSave>();
                actor.Set<Removing>(new Removing(RpcStatus.Error, "The actor's record does not serialize: " + e.Message));
            }

            return Outcome.Unwritable;
        }

        ulong hash = Records.Hash(record.Payload);
        if (hash == stored.Hash && stored.Attempt == 0) // a write in doubt may have stored another state: write this one
        {
            record.Dispose();
            return Outcome.Unchanged;
        }

        store.Saves.Add(actor, new StoredKey(target.HandlerType.Id, target.Key), stored.Version, record, hash, stored.Attempt, actor.Has<FinalSave>());
        actor.Set(new Saving());
        return Outcome.Queued;
    }

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, SaveBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) Log.SaveFailed(world.Logger, error, batch.Count);
        var versions = batch.Versions;
        var writes = batch.Writes;
        long now = world.GetSingleton<ShardActivity>().NowMs;
        for (int i = 0; i < batch.Count; i++)
        {
            var actor = batch.Entities[i];
            if (!actor.IsAlive || !actor.Has<Saving>()) continue;
            actor.Remove<Saving>();
            ref var stored = ref actor.Get<Stored>();
            if (error != null)
            {
                stored.RetryAtMs = now + 1000;
                stored.Attempt = writes[i].Hash; // it may have landed: the retry says so (the latest attempt only)
                // The failed write is asked for again (SaveNow): it is retried after the delay, not at the next
                // checkpoint (none with SaveEvery zero). A final save stays pending anyway.
                // A copy leaving by handoff does not wait for the store: its record carries the last written hash and
                // says the write was critical, so the new owner writes it at once.
                if (!batch.Finals[i] && actor.Has<HandOff>()) { actor.Set(new CriticalPending()); }
                else if (!batch.Finals[i] && !actor.Has<SaveNow>()) actor.Set(new SaveNow());
                continue;
            }

            if (versions[i] != 0)
            {
                stored.Version = versions[i];
                stored.Hash = writes[i].Hash;
                stored.Attempt = 0;
                // A checkpoint taken before an unload that began meanwhile: the final save writes what changed since
                // (Final, next wake; nothing when nothing did).
                if (!batch.Finals[i]) continue;
            }
            else
            {
                Log.RecordTaken(world.Logger, actor.Get<ActorRef>().Actor, writes[i].ExpectedVersion);
                if (!actor.Has<FinalSave>())
                {
                    // Also a copy already asked to leave: the key has a newer claimer, so this copy hands nothing on.
                    actor.Set(new UnloadRequested());
                    actor.Set(new Fenced());
                    continue;
                }
                // unloading: the key has a newer owner, nothing of this copy is written
            }

            actor.Remove<FinalSave>();
            actor.Set<Removing>(new Removing(RpcStatus.Ok, null));
        }

        batch.Clear();
    }
}

/// <summary>
/// Write-behind checkpoints, a few times a second (<see cref="ShardIdleScan"/>): an actor whose
/// <see cref="Actor.SaveEvery"/> passed has its record written if it changed, so a crash loses at most that much.
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed partial class CheckpointSystem : EcsSystem
{
    [ForEach, Without<Saving>, Without<FinalSave>, Without<Unloading>, Without<Removing>, Without<Turn>, Without<Fenced>]
    private static void Due(Entity actor, ref Stored stored, in ActorRef self, in ShardStore store, in ShardActivity activity)
    {
        if (stored.EveryMs > 0 && activity.NowMs >= stored.DueMs)
            PersistSystem.Queue(actor, ref stored, self.Actor, in store, activity.NowMs);
    }
}

/// <summary>Hands the shard's collected load and save batches to the store, one of each in flight at a time.</summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<PersistSystem>]
[UpdateAfter<HandoffSystem>]
internal sealed class StoreSystem : EcsSystem
{

    protected override void OnUpdate()
    {
        var store = World.GetSingleton<ShardStore>();
        if (!store.Enabled) return;
        if (!store.Loads.InFlight && store.Loads.Count > 0) store.Loads.Start(store.Store!);
        if (!store.Saves.InFlight && store.Saves.Count > 0) store.Saves.Start(store.Store!);
    }
}
