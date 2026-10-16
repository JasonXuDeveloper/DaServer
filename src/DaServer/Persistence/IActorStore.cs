namespace DaServer.Persistence;

/// <summary>An actor's record in a store: its handler type and key.</summary>
public readonly record struct StoredKey(uint HandlerTypeId, long Key);

/// <summary>A stored record: its version (0: none stored) and its bytes (sections of persisted components).</summary>
public readonly record struct StoredRecord(long Version, ReadOnlyMemory<byte> Data);

/// <summary>
/// A record to write if the store still holds <see cref="ExpectedVersion"/> for its key (0: none yet); it becomes
/// version <see cref="ExpectedVersion"/> + 1. A different stored version means another node owns the actor now.
/// </summary>
public readonly record struct StoredWrite(StoredKey Key, long ExpectedVersion, ReadOnlyMemory<byte> Data);

/// <summary>
/// Where actors' records live (ADR-068): all a backend implements. Calls come in batches (the loads and saves of one
/// shard wake) from shard threads and must not block them: complete asynchronously when IO is involved. The memory
/// passed in is valid until the returned task completes; the data of loaded records must stay valid until the next call.
/// </summary>
public interface IActorStore
{
    /// <summary>Fill <paramref name="records"/>[i] with the record of <paramref name="keys"/>[i] (version 0 when none).</summary>
    ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write every record whose key still holds its expected version, atomically per record;
    /// <paramref name="accepted"/>[i] says whether <paramref name="writes"/>[i] was written.
    /// </summary>
    ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken cancellationToken = default);
}

/// <summary>
/// Records in process memory: tests, local development, and a node that only needs state across unloads (lost when the
/// process exits). Thread-safe; versioned like a real store.
/// </summary>
public sealed class InMemoryActorStore : IActorStore
{
    private readonly ConcurrentDictionary<StoredKey, (long Version, byte[] Data)> _records = new();
    private readonly Lock _gate = new();

    /// <summary>Records stored (tests).</summary>
    public int Count => _records.Count;

    public ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default)
    {
        var from = keys.Span;
        var to = records.Span;
        for (int i = 0; i < from.Length; i++)
            to[i] = _records.TryGetValue(from[i], out var r) ? new StoredRecord(r.Version, r.Data) : default;
        return default;
    }

    public ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken cancellationToken = default)
    {
        var from = writes.Span;
        var to = accepted.Span;
        lock (_gate)
            for (int i = 0; i < from.Length; i++)
            {
                var write = from[i];
                long stored = _records.TryGetValue(write.Key, out var r) ? r.Version : 0;
                to[i] = stored == write.ExpectedVersion;
                if (to[i]) _records[write.Key] = (stored + 1, write.Data.ToArray()); // the caller reuses its buffer
            }

        return default;
    }
}
