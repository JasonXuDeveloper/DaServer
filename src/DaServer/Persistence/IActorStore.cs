using System.Runtime.InteropServices;

namespace DaServer.Persistence;

/// <summary>An actor's record in a store: its handler type and key.</summary>
public readonly record struct StoredKey(uint HandlerTypeId, long Key);

/// <summary>A stored record: its version and its bytes (sections of persisted components; empty when none was written).</summary>
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
    /// <summary>
    /// Load the records of <paramref name="keys"/> and claim them for the caller, atomically per record: the stored
    /// version goes up by one and <paramref name="records"/>[i] holds the record of <paramref name="keys"/>[i] at that
    /// new version, the one the caller's writes expect. A node that loaded the record before (a stale owner: a split, a
    /// pause past its death) expects an older version, so its writes are refused and it cannot overwrite the new
    /// owner's changes. A key without a record is claimed too: an empty record at version 1.
    /// </summary>
    ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write every record whose key still holds its expected version, atomically per record;
    /// <paramref name="accepted"/>[i] says whether <paramref name="writes"/>[i] was written.
    /// </summary>
    ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken cancellationToken = default);
}

/// <summary>
/// Records in process memory: tests, local development, and a node that only needs state across unloads (lost when the
/// process exits). Thread-safe and versioned like a real store, and like one it allocates nothing per write once a key
/// is known: each key keeps its own buffer, overwritten in place (grown only when a record outgrows it).
/// </summary>
public sealed class InMemoryActorStore : IActorStore
{
    /// <summary>One key's record: its version and its bytes (<see cref="Length"/> of <see cref="Buffer"/>).</summary>
    private sealed class Entry
    {
        public long Version;
        public byte[] Buffer = [];
        public int Length;
    }

    private readonly Dictionary<StoredKey, Entry> _records = new();
    private readonly Lock _gate = new();

    /// <summary>Records stored (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _records.Count;
        }
    }

    /// <summary>
    /// The loaded data is a copy (the stored buffer is overwritten by later writes): loads happen once per activation,
    /// writes every checkpoint.
    /// </summary>
    public ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default)
    {
        var from = keys.Span;
        var to = records.Span;
        lock (_gate)
            for (int i = 0; i < from.Length; i++)
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_records, from[i], out _);
                entry ??= new Entry();
                entry.Version++;
                to[i] = new StoredRecord(entry.Version, entry.Length == 0 ? default : entry.Buffer.AsSpan(0, entry.Length).ToArray());
            }

        return default;
    }

    /// <summary>Tests: a key's record as stored, without claiming it.</summary>
    internal StoredRecord Peek(StoredKey key)
    {
        lock (_gate)
            return _records.TryGetValue(key, out var entry) ? new StoredRecord(entry.Version, entry.Buffer.AsSpan(0, entry.Length).ToArray()) : default;
    }

    public ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken cancellationToken = default)
    {
        var from = writes.Span;
        var to = accepted.Span;
        lock (_gate)
            for (int i = 0; i < from.Length; i++)
            {
                var write = from[i];
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_records, write.Key, out bool exists);
                to[i] = (exists ? entry!.Version : 0) == write.ExpectedVersion;
                if (!to[i])
                {
                    if (!exists) _records.Remove(write.Key); // looking it up added it
                    continue;
                }

                entry ??= new Entry();
                if (entry.Buffer.Length < write.Data.Length) entry.Buffer = new byte[Math.Max(write.Data.Length, entry.Buffer.Length * 2)];
                write.Data.Span.CopyTo(entry.Buffer); // the caller reuses its buffer once this call returns
                entry.Length = write.Data.Length;
                entry.Version++;
            }

        return default;
    }
}
