using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>
/// A store decorator for persistence tests: counts what reaches the inner store and injects the faults and
/// interleavings a test forces (an outage, a hung connection, an answer lost after the commit, a write held in flight).
/// Every knob is off by default, so an unconfigured one only counts.
/// </summary>
internal sealed class TestStore(IActorStore inner) : IActorStore
{
    public readonly IActorStore Inner = inner;

    /// <summary>Keys the store was asked to load (claims that reached the inner store).</summary>
    public int Loads;

    /// <summary>The largest batch of keys one load asked for.</summary>
    public int LargestLoad;

    /// <summary>Writes that passed the faults and gate and went to the inner store, accepted or not.</summary>
    public int Writes;

    /// <summary>Write calls answered, successfully or not (a test waits for a retry's answer with it).</summary>
    public int Answered;

    /// <summary>Keys whose writes the store accepted, recorded once the write has landed.</summary>
    public readonly ConcurrentDictionary<long, bool> Written = new();

    /// <summary>Fail this many next loads with an <see cref="IOException"/> (an outage, a failover), then work.</summary>
    public int FailLoads;

    /// <summary>Fail this many next writes with an <see cref="IOException"/>, then work.</summary>
    public int FailWrites;

    /// <summary>This many next loads never finish until their token is cancelled (a dead connection).</summary>
    public int HangLoads;

    /// <summary>This many next writes never finish until their token is cancelled.</summary>
    public int HangWrites;

    /// <summary>
    /// This many next writes land in the inner store, then fail (the connection reset after the commit): the runtime
    /// must take the write for lost although it was stored.
    /// </summary>
    public int LoseAnswers;

    /// <summary>While set, every write fails at once (the store is down); loads still work.</summary>
    public volatile bool WritesDown;

    /// <summary>Holds loads before they reach the inner store.</summary>
    public readonly StoreGate LoadGate = new();

    /// <summary>Holds writes before they reach the inner store.</summary>
    public readonly StoreGate WriteGate = new();

    public ValueTask InitializeAsync(CancellationToken ct = default) => Inner.InitializeAsync(ct);

    public async ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default)
    {
        if (Interlocked.Decrement(ref FailLoads) >= 0) throw new IOException("store unavailable");
        if (Interlocked.Decrement(ref HangLoads) >= 0) await Task.Delay(Timeout.Infinite, ct);
        await LoadGate.Pass();
        Interlocked.Add(ref Loads, keys.Length);
        InterlockedMax(ref LargestLoad, keys.Length);
        await Inner.ClaimAsync(keys, records, ct);
    }

    public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<long> versions, CancellationToken ct = default)
    {
        try
        {
            if (WritesDown) throw new InvalidOperationException("the store is down");
            if (Interlocked.Decrement(ref FailWrites) >= 0) throw new IOException("store unavailable");
            await WriteGate.Pass();
            if (Interlocked.Decrement(ref HangWrites) >= 0) await Task.Delay(Timeout.Infinite, ct);
            Interlocked.Add(ref Writes, writes.Length);
            await Inner.SaveAsync(writes, versions, ct);
            for (int i = 0; i < writes.Length; i++)
                if (versions.Span[i] != 0) Written[writes.Span[i].Key.Key] = true;
            if (Interlocked.Decrement(ref LoseAnswers) >= 0) throw new IOException("the connection reset after the commit");
        }
        finally
        {
            Interlocked.Increment(ref Answered);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (int seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}

/// <summary>Holds the calls that pass it from <see cref="Hold"/> until <see cref="Release"/>; open by default.</summary>
internal sealed class StoreGate
{
    private TaskCompletionSource? _open;
    private int _held;
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Hold the next <paramref name="calls"/> calls (every call by default); <see cref="Started"/> starts over.</summary>
    public void Hold(int calls = int.MaxValue)
    {
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _held = calls;
        _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Let the held calls and every later one through.</summary>
    public void Release() => _open?.TrySetResult();

    /// <summary>A call reached the gate since the last <see cref="Hold"/> (or since construction), held or not.</summary>
    public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal Task Pass()
    {
        var open = _open;
        _started.TrySetResult();
        return open != null && Interlocked.Decrement(ref _held) >= 0 ? open.Task : Task.CompletedTask;
    }
}
