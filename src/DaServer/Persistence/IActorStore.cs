using System.Runtime.InteropServices;

namespace DaServer.Persistence;

/// <summary>An actor's record in a store: its handler type and key.</summary>
public readonly record struct StoredKey(uint HandlerTypeId, long Key);

/// <summary>A stored record: its version and its bytes (sections of persisted components; empty when none was written).</summary>
public readonly record struct StoredRecord(long Version, ReadOnlyMemory<byte> Data);

/// <summary>
/// A record to write if the store still holds <see cref="ExpectedVersion"/> for its key (0: none yet); it then gets the
/// next version. A different stored version means another node owns the actor now, with one exception: the owner's
/// previous write whose answer was lost (<see cref="Attempt"/>, the hash of what it sent; 0 when none). A store that
/// holds version <see cref="ExpectedVersion"/> + 1 with that hash took that write: it writes this one after it.
/// </summary>
/// <param name="Key">The actor.</param>
/// <param name="ExpectedVersion">The version the writer last read or wrote.</param>
/// <param name="Data">The record.</param>
/// <param name="Hash">The record's hash: stored with it, so a write retried after a lost answer can tell it landed.</param>
/// <param name="Attempt">The hash of the writer's previous write whose answer was lost, or 0.</param>
public readonly record struct StoredWrite(StoredKey Key, long ExpectedVersion, ReadOnlyMemory<byte> Data, ulong Hash, ulong Attempt);

/// <summary>
/// Where actors' records live (ADR-068): all a backend implements. Calls come in batches (the loads and saves of one
/// shard wake) from shard threads and must not block them: complete asynchronously when IO is involved. The memory
/// passed in is valid until the returned task completes; the data of loaded records must stay valid until the next call.
/// </summary>
public interface IActorStore
{
    /// <summary>
    /// Get ready before the node takes any actor (connect, create or check the schema): called once as the node starts,
    /// and the node does not start when it fails, so a missing database or table shows at start.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the records of <paramref name="keys"/> and claim them for the caller, atomically per record: the stored
    /// version goes up by one and <paramref name="records"/>[i] holds the record of <paramref name="keys"/>[i] at that
    /// new version, the one the caller's writes expect. A node that loaded the record before (a stale owner: a split, a
    /// pause past its death) expects an older version, so its writes are refused and it cannot overwrite the new
    /// owner's changes. A key without a record is claimed too: an empty record at version 1.
    /// </summary>
    ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write every record whose key still holds its expected version (or, after a lost answer, the version its
    /// <see cref="StoredWrite.Attempt"/> made), atomically per record, with its hash; <paramref name="versions"/>[i] is
    /// the version <paramref name="writes"/>[i] was written at, 0 when it was refused.
    /// </summary>
    ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<long> versions, CancellationToken cancellationToken = default);
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
        public ulong Hash; // of the last record written
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

    /// <summary>Nothing to prepare: memory is there.</summary>
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => default;

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

    public ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<long> versions, CancellationToken cancellationToken = default)
    {
        var from = writes.Span;
        var to = versions.Span;
        lock (_gate)
            for (int i = 0; i < from.Length; i++)
            {
                var write = from[i];
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_records, write.Key, out bool exists);
                long version = exists ? entry!.Version : 0;
                bool landed = write.Attempt != 0 && version == write.ExpectedVersion + 1 && entry!.Hash == write.Attempt;
                if (version != write.ExpectedVersion && !landed)
                {
                    to[i] = 0;
                    if (!exists) _records.Remove(write.Key); // looking it up added it
                    continue;
                }

                entry ??= new Entry();
                if (entry.Buffer.Length < write.Data.Length) entry.Buffer = new byte[Math.Max(write.Data.Length, entry.Buffer.Length * 2)];
                write.Data.Span.CopyTo(entry.Buffer); // the caller reuses its buffer once this call returns
                entry.Length = write.Data.Length;
                entry.Hash = write.Hash;
                to[i] = ++entry.Version;
            }

        return default;
    }
}
