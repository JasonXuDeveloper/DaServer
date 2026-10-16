namespace DaServer.Persistence;

/// <summary>
/// Collects the actors waiting for their record (<see cref="AwaitingRecord"/>) into the shard's load batch, and applies
/// the batch when the store answered: components restored, then <see cref="Actor.OnLoad"/> starts as the first turn.
/// </summary>
[UpdateInGroup<ShardMessages>]
internal sealed partial class LoadSystem : EcsSystem
{
    public override int Priority => 2;

    [ForEach, With<AwaitingRecord>]
    private static void Collect(Entity actor, in ActorRef self, in ShardStore store)
    {
        if (store.Loads.InFlight) return; // the next batch takes it
        var target = self.Actor;
        actor.Remove<AwaitingRecord>();
        actor.Add<RecordRequested>();
        store.Loads.Add(actor, new StoredKey(target.HandlerType.Id, target.Key));
    }

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, LoadBatch batch)
    {
        var error = batch.TakeResult();
        var store = world.GetSingleton<ShardStore>();
        var records = batch.Records;
        var entities = batch.Entities;
        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (!entity.IsAlive || !entity.Has<RecordRequested>()) continue; // removed meanwhile (fenced)
            entity.Remove<RecordRequested>();
            var actor = entity.Get<ActorRef>().Actor;
            try
            {
                if (error != null) throw error;
                Records.Read(entity, records[i].Data.Span, store.ById);
                // The hash of what a save would write now: an actor whose record does not change is not written again.
                var fresh = Records.Write(entity, store.Types);
                ulong hash = Records.Hash(fresh.Payload);
                fresh.Dispose();
                long every = (long)actor.SaveEvery.TotalMilliseconds;
                entity.Add<Stored>() = new Stored
                {
                    Version = records[i].Version, Hash = hash, EveryMs = every, DueMs = Environment.TickCount64 + every,
                };
            }
            catch (Exception e)
            {
                world.Logger().LogError(e, "Loading the record of {Actor} failed", actor);
                entity.Add<Removing>() = new Removing(RpcStatus.Error, "Loading the actor's record failed: " + e.Message);
                continue;
            }

            TurnWatcher.Begin(world, entity, ShardDispatch.Start(static a => a.OnLoad().AsValueTask(), actor));
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

    [ForEach, With<SaveNow>, Without<Saving>]
    private static void Now(Entity actor, ref Stored stored, in ActorRef self, in ShardStore store)
    {
        if (Queue(actor, ref stored, self.Actor, in store) != Outcome.Busy) actor.Remove<SaveNow>();
    }

    [ForEach, With<FinalSave>, Without<Saving>]
    private static void Final(Entity actor, ref Stored stored, in ActorRef self, in ShardStore store)
    {
        if (Queue(actor, ref stored, self.Actor, in store) != Outcome.Unchanged) return;
        actor.Remove<FinalSave>();
        actor.Add<Removing>() = new Removing(RpcStatus.Ok, null); // nothing changed since the last write
    }

    /// <summary>
    /// Serialize the actor's record into the shard's save batch unless it equals what was last written. The ref
    /// <paramref name="stored"/> is invalid afterwards (the actor gains <see cref="Saving"/>).
    /// </summary>
    internal static Outcome Queue(Entity actor, ref Stored stored, Actor target, in ShardStore store)
    {
        long now = Environment.TickCount64;
        if (now < stored.RetryAtMs || store.Saves.InFlight || store.Saves.Full) return Outcome.Busy;
        if (stored.EveryMs > 0) stored.DueMs = now + stored.EveryMs;
        var record = Records.Write(actor, store.Types);
        ulong hash = Records.Hash(record.Payload);
        if (hash == stored.Hash)
        {
            record.Dispose();
            return Outcome.Unchanged;
        }

        store.Saves.Add(actor, new StoredKey(target.HandlerType.Id, target.Key), stored.Version, record, hash);
        actor.Add<Saving>();
        return Outcome.Queued;
    }

    /// <summary>The store answered <paramref name="batch"/> (shard thread).</summary>
    internal static void Apply(World world, SaveBatch batch)
    {
        var error = batch.TakeResult();
        if (error != null) world.Logger().LogError(error, "Saving {Count} records failed: retried in a second", batch.Count);
        var accepted = batch.Accepted;
        var writes = batch.Writes;
        long now = Environment.TickCount64;
        for (int i = 0; i < batch.Count; i++)
        {
            var actor = batch.Entities[i];
            if (!actor.IsAlive || !actor.Has<Saving>()) continue;
            actor.Remove<Saving>();
            ref var stored = ref actor.Get<Stored>();
            if (error != null)
            {
                stored.RetryAtMs = now + 1000;
                continue;
            }

            bool final = actor.Has<FinalSave>();
            if (accepted[i])
            {
                stored.Version = writes[i].ExpectedVersion + 1;
                stored.Hash = batch.Hashes[i];
            }
            else
            {
                world.Logger().LogError("The record of {Actor} was written by another node (expected version {Version}): " +
                                        "this node no longer owns it, its copy is dropped", actor.Get<ActorRef>().Actor, writes[i].ExpectedVersion);
                if (!final && !actor.Has<UnloadRequested>())
                {
                    actor.Add<UnloadRequested>();
                    actor.Add<Fenced>();
                }
            }

            if (!final) continue;
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
    [ForEach, Without<Saving>, Without<FinalSave>, Without<Unloading>, Without<Removing>]
    private static void Due(Entity actor, ref Stored stored, in ActorRef self, in ShardStore store)
    {
        if (stored.EveryMs > 0 && Environment.TickCount64 >= stored.DueMs) PersistSystem.Queue(actor, ref stored, self.Actor, in store);
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
