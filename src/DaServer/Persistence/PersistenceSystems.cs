namespace DaServer.Persistence;

/// <summary>
/// Collects the actors waiting for their record (<see cref="AwaitingRecord"/>) into the shard's load batch, and applies
/// the batch when the store answered: components restored, then <see cref="Actor.OnLoad"/> starts as the first turn.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class LoadSystem : EcsSystem
{
    public override int Priority => 2;

    [ForEach]
    private static void Collect(Entity actor, in AwaitingRecord awaiting, in ActorRef self, in ShardStore store, in ShardActivity activity)
    {
        if (store.Loads.InFlight || store.Loads.Full || activity.NowMs < awaiting.NotBeforeMs) return; // a later batch takes it
        var (target, since) = (self.Actor, awaiting.SinceMs);
        actor.Remove<AwaitingRecord>(); // `awaiting` is invalid from here on
        actor.Add<RecordRequested>() = new RecordRequested(since);
        store.Loads.Add(actor, new StoredKey(target.HandlerType.Id, target.Key));
    }

    /// <summary>A failed load is tried again this long after (a store restarting, a failover).</summary>
    private const long RetryLoadMs = 200;

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, LoadBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) Log.LoadFailed(world.Logger(), error, batch.Count);
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
            if (error != null && now - requested.SinceMs < (long)world.Shard().Node.Options.CallTimeout.TotalMilliseconds)
            {
                // A store outage delays the actor (its mail waits) instead of failing it: tried again shortly.
                entity.Add<AwaitingRecord>() = new AwaitingRecord(requested.SinceMs, now + RetryLoadMs);
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
                entity.Add<Stored>() = new Stored
                {
                    Version = records[i].Version, Hash = hash, EveryMs = every, DueMs = world.GetSingleton<ShardActivity>().NowMs + every,
                };
            }
            catch (Exception e)
            {
                if (e != error) Log.RestoreFailed(world.Logger(), e, actor); // the batch's error is logged once
                entity.Add<Removing>() = new Removing(RpcStatus.Error, "Loading the actor's record failed: " + e.Message);
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
internal sealed partial class PersistSystem : EcsSystem
{
    public override int Priority => 6;

    internal enum Outcome { Queued, Unchanged, Busy }

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
        actor.Add<Removing>() = new Removing(RpcStatus.Ok, null); // nothing changed since the last write
    }

    /// <summary>
    /// Serialize the actor's record into the shard's save batch unless it equals what was last written. The ref
    /// <paramref name="stored"/> is invalid afterwards (the actor gains <see cref="Saving"/>).
    /// </summary>
    internal static Outcome Queue(Entity actor, ref Stored stored, Actor target, in ShardStore store, long now)
    {
        if (now < stored.RetryAtMs || store.Saves.InFlight || store.Saves.Full) return Outcome.Busy;
        if (stored.EveryMs > 0) stored.DueMs = now + stored.EveryMs;
        var record = Records.Write(actor, store.Types);
        ulong hash = Records.Hash(record.Payload);
        if (hash == stored.Hash)
        {
            record.Dispose();
            return Outcome.Unchanged;
        }

        store.Saves.Add(actor, new StoredKey(target.HandlerType.Id, target.Key), stored.Version, record, hash, actor.Has<FinalSave>());
        actor.Add<Saving>();
        return Outcome.Queued;
    }

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, SaveBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) Log.SaveFailed(world.Logger(), error, batch.Count);
        var accepted = batch.Accepted;
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
                // The failed write is asked for again (SaveNow): it is retried after the delay, not at the next
                // checkpoint (none with SaveEvery zero). A final save stays pending anyway.
                if (!batch.Finals[i] && !actor.Has<SaveNow>()) actor.Add<SaveNow>();
                continue;
            }

            if (accepted[i])
            {
                stored.Version = writes[i].ExpectedVersion + 1;
                stored.Hash = batch.Hashes[i];
                // A checkpoint taken before an unload that began meanwhile: the final save writes what changed since
                // (Final, next wake; nothing when nothing did).
                if (!batch.Finals[i]) continue;
            }
            else
            {
                Log.RecordTaken(world.Logger(), actor.Get<ActorRef>().Actor, writes[i].ExpectedVersion);
                if (!actor.Has<FinalSave>())
                {
                    if (!actor.Has<UnloadRequested>())
                    {
                        actor.Add<UnloadRequested>();
                        actor.Add<Fenced>();
                    }

                    continue;
                }
                // unloading: the key has a newer owner, nothing of this copy is written
            }

            actor.Remove<FinalSave>();
            actor.Add<Removing>() = new Removing(RpcStatus.Ok, null);
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
internal sealed class StoreSystem : EcsSystem
{
    public override int Priority => 7;

    protected override void OnUpdate()
    {
        var store = World.GetSingleton<ShardStore>();
        if (!store.Enabled) return;
        if (!store.Loads.InFlight && store.Loads.Count > 0) store.Loads.Start(store.Store!);
        if (!store.Saves.InFlight && store.Saves.Count > 0) store.Saves.Start(store.Store!);
    }
}
