namespace DaServer.Persistence;

// Persistence as ECS data (ADR-068): an actor's stored record is a component, the steps of loading and saving are
// tags, the shard's store and its batches are a singleton. LoadSystem, PersistSystem, ChangeSystem, CheckpointSystem and StoreSystem
// each do one step.

/// <summary>
/// The actor's record as last loaded or written: its version (0: none stored), the hash of those bytes (an unchanged
/// record is not written again) and when the next checkpoint is due (<see cref="Actor.SaveEvery"/>).
/// </summary>
internal struct Stored : IComponent
{
    public long Version;
    public ulong Hash;

    /// <summary>
    /// The hash of a write whose answer was lost (a store error: it may have landed), 0 when none: the next write says
    /// so, and a store that took it writes after it instead of refusing this owner as a stale one.
    /// </summary>
    public ulong Attempt;
    public long DueMs;
    public long EveryMs;

    /// <summary>After a failed write: no other attempt before this (<see cref="ShardActivity.NowMs"/>).</summary>
    public long RetryAtMs;
}

/// <summary>
/// The actor waits for its record before <see cref="Actor.OnLoad"/> (the shard's next load batch takes it, not before
/// <see cref="NotBeforeMs"/>: a failed load is retried after a delay, until the call timeout since <see cref="SinceMs"/>).
/// </summary>
internal readonly record struct AwaitingRecord(long SinceMs, long NotBeforeMs) : IComponent;

/// <summary>The actor's record is being loaded (asked for first at <see cref="SinceMs"/>).</summary>
internal readonly record struct RecordRequested(long SinceMs) : IComponent;

/// <summary>
/// Tag: a stored component of the actor was written since its record was last serialized (<see cref="ChangeSystem"/>);
/// only such actors are checkpointed.
/// </summary>
internal struct Unsaved : IComponent;

/// <summary>Tag: a write of the actor's record is in flight (no other is started meanwhile).</summary>
internal struct Saving : IComponent;

/// <summary>Tag: <see cref="Actor.OnUnload"/> ran; the actor is removed once its record is written.</summary>
internal struct FinalSave : IComponent;

/// <summary>
/// The shard's store, the component types of records (<see cref="Types"/> stored, <see cref="Moved"/> handed to a new
/// owner: stored and [Handoff] ones) and the batches it exchanges with the store.
/// </summary>
internal readonly record struct ShardStore(IActorStore? Store, PersistentType[] Types, PersistentType[] Moved,
    FrozenDictionary<uint, PersistentType> ById, LoadBatch Loads, SaveBatch Saves) : ISingletonComponent
{
    /// <summary>Whether actors of this shard load and save records at all.</summary>
    public bool Enabled => Store != null;

    /// <summary>Whether actors changing node take a record along (ADR-072).</summary>
    public bool HandsOff => Moved.Length > 0;
}

/// <summary>
/// One store call a shard has in flight (a load or a save batch): started on the shard, its result posted back to it,
/// bounded by <see cref="NodeOptions.StoreTimeout"/>. Reused: a steady stream of calls allocates nothing in the shard.
/// </summary>
internal abstract class StoreCall(Shard shard)
{
    private static readonly Action<object?> Apply = static b => ((StoreCall)b!).Applied();

    private ValueTask _task;
    private Action? _completed;
    private readonly StoreDeadline _deadline = new();

    protected Shard Shard => shard;

    public bool InFlight { get; private set; }

    /// <summary>The store call itself.</summary>
    protected abstract ValueTask Call(IActorStore store, CancellationToken cancellationToken);

    /// <summary>Back on the shard thread: apply the results.</summary>
    protected abstract void Applied();

    /// <summary>Hand the batch to the store; its results come back to the shard thread (<see cref="Applied"/>).</summary>
    public void Start(IActorStore store)
    {
        InFlight = true;
        try
        {
            _task = Call(store, _deadline.Start(shard));
        }
        catch (Exception e)
        {
            _task = ValueTask.FromException(e);
        }

        if (_task.IsCompleted) shard.Post(Apply, this);
        else _task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_completed ??= () => shard.Post(Apply, this));
    }

    /// <summary>The result of the call (null) or why it failed; resets the task.</summary>
    public Exception? TakeResult()
    {
        var task = _task;
        _task = default;
        _deadline.Stop();
        try
        {
            task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>The batch is empty and the next call may start.</summary>
    protected void Done() => InFlight = false;
}

/// <summary>
/// The loads of one shard wake, handed to the store as one call; one batch is in flight at a time, so a shard holds a
/// bounded number of pending loads.
/// </summary>
internal sealed class LoadBatch(Shard shard) : StoreCall(shard)
{
    /// <summary>Loads per batch: an actor beyond it waits for the next one (a store that was slow sees bounded statements).</summary>
    public const int Max = 512;

    public readonly List<Entity> Entities = new();
    private StoredKey[] _keys = new StoredKey[64];
    private StoredRecord[] _records = new StoredRecord[64];

    public int Count => Entities.Count;

    public bool Full => Count == Max;

    public void Add(Entity entity, StoredKey key)
    {
        if (Entities.Count == _keys.Length)
        {
            Array.Resize(ref _keys, _keys.Length * 2);
            Array.Resize(ref _records, _records.Length * 2);
        }

        _keys[Entities.Count] = key;
        Entities.Add(entity);
    }

    protected override ValueTask Call(IActorStore store, CancellationToken cancellationToken) =>
        store.ClaimAsync(_keys.AsMemory(0, Count), _records.AsMemory(0, Count), cancellationToken);

    protected override void Applied() => LoadSystem.Apply(Shard.World, this);

    public ReadOnlySpan<StoredRecord> Records => _records.AsSpan(0, Count);

    public void Clear()
    {
        Array.Clear(_records, 0, Count);
        Entities.Clear();
        Done();
    }
}

/// <summary>The writes of one shard wake (at most <see cref="Max"/>), handed to the store as one call; one in flight at a time.</summary>
internal sealed class SaveBatch(Shard shard) : StoreCall(shard)
{
    /// <summary>Writes per batch: an actor beyond it waits for the next one (its checkpoint stays due).</summary>
    public const int Max = 512;

    public readonly List<Entity> Entities = new();
    public readonly List<FrameWriter> Frames = new();

    /// <summary>Whether each write is its actor's final save (queued after its unload), not a checkpoint taken before.</summary>
    public readonly List<bool> Finals = new();
    private readonly StoredWrite[] _writes = new StoredWrite[Max];
    private readonly long[] _versions = new long[Max];

    public int Count => Entities.Count;

    public bool Full => Count == Max;

    public void Add(Entity entity, StoredKey key, long expectedVersion, FrameWriter record, ulong hash, ulong attempt, bool final)
    {
        _writes[Count] = new StoredWrite(key, expectedVersion, record.WrittenMemory[FrameHeader.Size..], hash, attempt);
        Entities.Add(entity);
        Frames.Add(record);
        Finals.Add(final);
    }

    protected override ValueTask Call(IActorStore store, CancellationToken cancellationToken) =>
        store.SaveAsync(_writes.AsMemory(0, Count), _versions.AsMemory(0, Count), cancellationToken);

    protected override void Applied() => PersistSystem.Apply(Shard.World, this);

    /// <summary>The version each write was written at, 0 when refused.</summary>
    public ReadOnlySpan<long> Versions => _versions.AsSpan(0, Count);

    public ReadOnlySpan<StoredWrite> Writes => _writes.AsSpan(0, Count);

    public void Clear()
    {
        foreach (var frame in Frames) frame.Dispose();
        Frames.Clear();
        Entities.Clear();
        Finals.Clear();
        Array.Clear(_writes);
        Done();
    }
}

/// <summary>
/// The cancellation of a store call after <see cref="NodeOptions.StoreTimeout"/>: one source per batch, reset after each
/// call, so timing calls allocates nothing (a new source only after one fired).
/// </summary>
internal sealed class StoreDeadline
{
    private CancellationTokenSource _source = new();

    public CancellationToken Start(Shard shard)
    {
        _source.CancelAfter(shard.Node.Options.StoreTimeout);
        return _source.Token;
    }

    public void Stop()
    {
        if (_source.TryReset()) return;
        _source.Dispose();
        _source = new CancellationTokenSource();
    }
}
