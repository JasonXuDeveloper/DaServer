namespace DaServer.Persistence;

// Persistence as ECS data (ADR-068): an actor's stored record is a component, the steps of loading and saving are
// tags, the shard's store and its batches are a singleton. LoadSystem, PersistSystem, CheckpointSystem and StoreSystem
// each do one step.

/// <summary>
/// The actor's record as last loaded or written: its version (0: none stored), the hash of those bytes (an unchanged
/// record is not written again) and when the next checkpoint is due (<see cref="Actor.SaveEvery"/>).
/// </summary>
internal struct Stored : IComponent
{
    public long Version;
    public ulong Hash;
    public long DueMs;
    public long EveryMs;

    /// <summary>After a failed write: no other attempt before this (<see cref="ShardActivity.NowMs"/>).</summary>
    public long RetryAtMs;
}

/// <summary>Tag: the actor waits for its record before <see cref="Actor.OnLoad"/> (the shard's next load batch takes it).</summary>
internal struct AwaitingRecord : IComponent;

/// <summary>Tag: the actor's record is being loaded.</summary>
internal struct RecordRequested : IComponent;

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
/// The loads of one shard wake, handed to the store as one call; one batch is in flight at a time, so a shard holds a
/// bounded number of pending loads. Reused: a steady stream of loads allocates nothing in the shard.
/// </summary>
internal sealed class LoadBatch(Shard shard)
{
    private static readonly Action<object?> Apply = static b => ((LoadBatch)b!).Applied();

    public readonly List<Entity> Entities = new();
    private StoredKey[] _keys = new StoredKey[64];
    private StoredRecord[] _records = new StoredRecord[64];
    private ValueTask _task;
    private Action? _completed;

    public bool InFlight { get; private set; }

    public int Count => Entities.Count;

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

    /// <summary>Hand the batch to the store; its results come back to the shard thread (<see cref="LoadSystem.Apply"/>).</summary>
    public void Start(IActorStore store)
    {
        InFlight = true;
        try
        {
            _task = store.LoadAsync(_keys.AsMemory(0, Count), _records.AsMemory(0, Count));
        }
        catch (Exception e)
        {
            _task = ValueTask.FromException(e);
        }

        if (_task.IsCompleted) shard.Post(Apply, this);
        else _task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_completed ??= () => shard.Post(Apply, this));
    }

    private void Applied() => LoadSystem.Apply(shard.World, this);

    /// <summary>The result of the call (null) or why it failed; resets the task.</summary>
    public Exception? TakeResult()
    {
        var task = _task;
        _task = default;
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

    public ReadOnlySpan<StoredRecord> Records => _records.AsSpan(0, Count);

    public void Clear()
    {
        Entities.Clear();
        Array.Clear(_records, 0, Math.Min(_records.Length, _keys.Length));
        InFlight = false;
    }
}

/// <summary>The writes of one shard wake (at most <see cref="Max"/>), handed to the store as one call; one in flight at a time.</summary>
internal sealed class SaveBatch(Shard shard)
{
    /// <summary>Writes per batch: an actor beyond it waits for the next one (its checkpoint stays due).</summary>
    public const int Max = 512;

    private static readonly Action<object?> Apply = static b => ((SaveBatch)b!).Applied();

    public readonly List<Entity> Entities = new();
    public readonly List<ulong> Hashes = new();
    public readonly List<FrameWriter> Frames = new();
    private readonly StoredWrite[] _writes = new StoredWrite[Max];
    private readonly bool[] _accepted = new bool[Max];
    private ValueTask _task;
    private Action? _completed;

    public bool InFlight { get; private set; }

    public int Count => Entities.Count;

    public bool Full => Count == Max;

    public void Add(Entity entity, StoredKey key, long expectedVersion, FrameWriter record, ulong hash)
    {
        _writes[Count] = new StoredWrite(key, expectedVersion, record.WrittenMemory[FrameHeader.Size..]);
        Entities.Add(entity);
        Hashes.Add(hash);
        Frames.Add(record);
    }

    public void Start(IActorStore store)
    {
        InFlight = true;
        try
        {
            _task = store.SaveAsync(_writes.AsMemory(0, Count), _accepted.AsMemory(0, Count));
        }
        catch (Exception e)
        {
            _task = ValueTask.FromException(e);
        }

        if (_task.IsCompleted) shard.Post(Apply, this);
        else _task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_completed ??= () => shard.Post(Apply, this));
    }

    private void Applied() => PersistSystem.Apply(shard.World, this);

    public Exception? TakeResult()
    {
        var task = _task;
        _task = default;
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

    public ReadOnlySpan<bool> Accepted => _accepted.AsSpan(0, Count);

    public ReadOnlySpan<StoredWrite> Writes => _writes.AsSpan(0, Count);

    public void Clear()
    {
        foreach (var frame in Frames) frame.Dispose();
        Frames.Clear();
        Entities.Clear();
        Hashes.Clear();
        Array.Clear(_writes);
        InFlight = false;
    }
}
