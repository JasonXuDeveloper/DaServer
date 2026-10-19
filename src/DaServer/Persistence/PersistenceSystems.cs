namespace DaServer.Persistence;

/// <summary>
/// Collects the actors waiting for their record (<see cref="AwaitingRecord"/>) into the shard's load batch, and applies
/// the batch when the store answered: components restored, then the actor is live (its first mail is <see cref="Loaded"/>
/// or <see cref="Created"/>).
/// </summary>
[UpdateInGroup<ShardMessages>]
[UpdateAfter<DispatchSystem>]
internal sealed class LoadSystem : EcsSystem
{
    [ForEach]
    private static void Collect(Entity actor, in AwaitingRecord awaiting, in ActorKey key, ref ShardStore store, in ShardActivity activity)
    {
        if (store.Loads.InFlight || store.Loads.Full || activity.NowMs < awaiting.NotBeforeMs) return; // a later batch takes it
        var (stored, since) = (new StoredKey(key.HandlerTypeId, key.Key), awaiting.SinceMs);
        actor.RemoveAwaitingRecord(); // `awaiting` is invalid from here on
        actor.SetRecordRequested(since);
        store.Loads.Add(actor, stored);
    }

    /// <summary>A failed load is tried again this long after (a store restarting, a failover).</summary>
    private const long RetryLoadMs = 200;

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, LoadBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) Log.LoadFailed(world.Logger, error, batch.Count);
        var store = world.ShardStore;
        var records = batch.Records;
        var entities = batch.Entities;
        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!entity.IsAlive || !entity.TryGetRecordRequested(out var requested)) continue; // removed meanwhile (fenced)
            entity.RemoveRecordRequested();
            var key = entity.ActorKey;
            var handlerType = world.Shard.Node.HandlerTypeById(key.HandlerTypeId)!;
            long now = world.ShardActivity.NowMs;
            if (error != null && now - requested.SinceMs < (long)world.Shard.Node.Options.CallTimeout.TotalMilliseconds)
            {
                // A store outage delays the actor (its mail waits) instead of failing it: tried again shortly.
                entity.SetAwaitingRecord(requested.SinceMs, now + RetryLoadMs);
                continue;
            }

            bool restored;
            try
            {
                if (error != null) throw error;
                restored = Records.Read(entity, records[i].Data.Span, store.ById);
                // The hash of what a save would write now: an actor whose record does not change is not written again.
                var fresh = Records.Write(entity, store.Types);
                ulong hash = Records.Hash(fresh.Payload);
                fresh.Dispose();
                long every = handlerType.Settings.SaveEverySeconds * 1000L;
                entity.Set(new Stored
                {
                    Version = records[i].Version, Hash = hash, EveryMs = every, DueMs = world.ShardActivity.NowMs + every,
                });
            }
            catch (Exception e)
            {
                if (e != error) Log.RestoreFailed(world.Logger, e, handlerType, key.Key); // the batch's error is logged once
                entity.SetRemoving(RpcStatus.Error);
                continue;
            }

            if (!ShardDispatch.BeginLive(world, entity, restored)) entity.SetRemoving(RpcStatus.Error); // its mail fails
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
[UpdateAfter<MailSystem>]
internal sealed class PersistSystem : EcsSystem
{
    internal enum Outcome { Queued, Unchanged, Busy, Unwritable }

    // Records are the state after whole messages (handlers run to completion). A fenced or removed actor writes
    // nothing: its state is half-changed (a failed handler) or its key has a new owner.
    [ForEach, With<SaveNow>, Without<Saving>, Without<Fenced>, Without<Removing>]
    private static void Now(Entity actor, ref Stored stored, in ActorKey key, in ShardStore store, in ShardActivity activity, World world)
    {
        if (Queue(world, actor, ref stored, key, in store, activity.NowMs) != Outcome.Busy) actor.RemoveSaveNow();
    }

    /// <summary>Without a store (no <see cref="Stored"/>) there is nothing to write: the request is dropped, so it holds no handoff.</summary>
    [ForEach, With<SaveNow>, Without<Stored>]
    private static void Nowhere(Entity actor) => actor.RemoveSaveNow();

    [ForEach, With<FinalSave>, Without<Saving>, Without<Fenced>]
    private static void Final(Entity actor, ref Stored stored, in ActorKey key, in ShardStore store, in ShardActivity activity, World world)
    {
        if (Queue(world, actor, ref stored, key, in store, activity.NowMs) != Outcome.Unchanged) return;
        actor.RemoveFinalSave();
        actor.SetRemoving(RpcStatus.Ok); // nothing changed since the last write
    }

    /// <summary>
    /// Serialize the actor's record into the shard's save batch unless it equals what was last written. The ref
    /// <paramref name="stored"/> is invalid afterwards (the actor gains <see cref="Saving"/>).
    /// </summary>
    internal static Outcome Queue(World world, Entity actor, ref Stored stored, ActorKey key, in ShardStore store, long now)
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
            var shard = world.Shard;
            if (shard.ErrorLogDue(out int unlogged))
                Log.RecordUnwritable(shard.Node.Logger, e, shard.Node.HandlerTypeById(key.HandlerTypeId)!, key.Key, unlogged);
            if (actor.IsFinalSave)
            {
                actor.RemoveFinalSave();
                actor.SetRemoving(RpcStatus.Error);
            }

            Serialized(actor);
            return Outcome.Unwritable;
        }

        // A write may leave the record as it was (the same value again): compared, not written.
        ulong hash = Records.Hash(record.Payload);
        if (hash == stored.Hash && stored.Attempt == 0) // a write in doubt may have stored another state: write this one
        {
            record.Dispose();
            Serialized(actor);
            return Outcome.Unchanged;
        }

        store.Saves.Add(actor, new StoredKey(key.HandlerTypeId, key.Key), stored.Version, record, hash, stored.Attempt, actor.IsFinalSave);
        actor.SetSaving();
        Serialized(actor);
        return Outcome.Queued;
    }

    /// <summary>The record was taken as it is now: a later write tags the actor again (structural: after the last use of the refs).</summary>
    private static void Serialized(Entity actor)
    {
        if (actor.IsUnsaved) actor.RemoveUnsaved();
    }

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, SaveBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) Log.SaveFailed(world.Logger, error, batch.Count);
        var versions = batch.Versions;
        var writes = batch.Writes;
        long now = world.ShardActivity.NowMs;
        for (int i = 0; i < batch.Count; i++)
        {
            var actor = batch.Entities[i];
            if (!actor.IsAlive || !actor.IsSaving) continue;
            actor.RemoveSaving();
            ref var stored = ref actor.Stored;
            if (error != null)
            {
                stored.RetryAtMs = now + 1000;
                stored.Attempt = writes[i].Hash; // it may have landed: the retry says so (the latest attempt only)
                // The failed write is asked for again (SaveNow): it is retried after the delay, not at the next
                // checkpoint (none with SaveEvery zero). A final save stays pending anyway.
                // A copy leaving by handoff does not wait for the store: its record carries the last written hash and
                // says the write was critical, so the new owner writes it at once.
                if (!batch.Finals[i] && actor.HasHandOff) { actor.SetCriticalPending(); }
                else if (!batch.Finals[i] && !actor.IsSaveNow) actor.SetSaveNow();
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
                var key = actor.ActorKey;
                Log.RecordTaken(world.Logger, world.Shard.Node.HandlerTypeById(key.HandlerTypeId)!, key.Key, writes[i].ExpectedVersion);
                if (!actor.IsFinalSave)
                {
                    // Also a copy already asked to leave: the key has a newer claimer, so this copy hands nothing on.
                    actor.SetUnloadRequested();
                    actor.SetFenced();
                    continue;
                }
                // unloading: the key has a newer owner, nothing of this copy is written
            }

            actor.RemoveFinalSave();
            actor.SetRemoving(RpcStatus.Ok);
        }

        batch.Clear();
    }
}

/// <summary>
/// Tags the actors whose stored components were written since its last run <see cref="Unsaved"/>: one bulk pass per
/// stored type over the change ticks EasyEcs keeps (ITrackChanges), so an unchanged actor costs nothing.
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed class ChangeSystem : EcsSystem
{
    protected override void OnUpdate()
    {
        var store = World.ShardStore;
        if (!store.Enabled) return;
        foreach (var type in store.Types) type.MarkWritten?.Invoke(World, LastRunTick);
    }
}

/// <summary>
/// Write-behind checkpoints, a few times a second (<see cref="ShardIdleScan"/>): a changed actor whose
/// <see cref="ActorSettings.SaveEverySeconds"/> passed has its record written, so a crash loses at most that much.
/// </summary>
[UpdateInGroup<ShardIdleScan>]
[UpdateAfter<ChangeSystem>]
internal sealed class CheckpointSystem : EcsSystem
{
    [ForEach, With<Unsaved>, Without<Saving>, Without<FinalSave>, Without<Leaving>, Without<Removing>, Without<Fenced>]
    private static void Due(Entity actor, ref Stored stored, in ActorKey key, in ShardStore store, in ShardActivity activity, World world)
    {
        if (stored.EveryMs > 0 && activity.NowMs >= stored.DueMs)
            PersistSystem.Queue(world, actor, ref stored, key, in store, activity.NowMs);
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
        var store = World.ShardStore;
        if (!store.Enabled) return;
        if (!store.Loads.InFlight && store.Loads.Count > 0) store.Loads.Start(store.Store!);
        if (!store.Saves.InFlight && store.Saves.Count > 0) store.Saves.Start(store.Store!);
    }
}
