using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>
/// Actor records (ADR-068) at the edges: a write in flight while the actor unloads, fails or reloads. A gate holds the
/// store's writes, so each interleaving is forced instead of left to a machine's speed.
/// </summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class PersistenceEdgeTests(string kind) : ClusterTest
{
    private readonly Stores _stores = new(kind);

    [OneTimeTearDown]
    public Task DropStores() => _stores.DisposeAsync().AsTask();

    /// <summary>Counts writes; <see cref="Hold"/> keeps them from the store until <see cref="Release"/>.</summary>
    private sealed class GatedStore(IActorStore inner) : IActorStore
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public int Writes;
        private TaskCompletionSource? _gate;
        private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold()
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release() => _gate?.TrySetResult();

        /// <summary>A write reached the store while held.</summary>
        public Task Started => _started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default) =>
            inner.ClaimAsync(keys, records, ct);

        public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default)
        {
            var gate = _gate;
            _started.TrySetResult();
            if (gate != null) await gate.Task;
            Interlocked.Add(ref Writes, writes.Length);
            await inner.SaveAsync(writes, accepted, ct);
        }
    }

    private Task<Node> StartWith(IActorStore store, string name = "single") => Start(name, configure: o => o.ActorStore = store);

    [Test]
    public async Task ARecordThatDoesNotSerializeHoldsNoOtherWrite()
    {
        // One actor's state stops serializing (a cycle) and it asks for a critical write, then the node stops: that
        // write is given up (the store keeps its last record), and every other actor's writes and the stop go on.
        Faults.Allow("RecordUnwritable"); // what the test provokes
        var store = new GatedStore(_stores.Create());
        var node = await StartWith(store);
        Assert.That(await node.Ask(new TangleId(1), new Tie()), Is.True);
        for (int key = 300; key < 310; key++) Assert.That(await node.Ask(new BankId(key), new Deposit(key, Critical: true)), Is.EqualTo(key));
        await Stop(node);
        Assert.That(Faults.Count("RecordUnwritable"), Is.GreaterThan(0), "the tangle's record did not serialize");

        var again = await StartWith(store, "again");
        for (int key = 300; key < 310; key++) Assert.That(await again.Ask(new BankId(key), new Balance()), Is.EqualTo(key), $"key {key} was written");
        Assert.That(await again.Ask(new TangleId(1), new Tie()), Is.True, "the tangle loads again (no record of it was ever written)");
    }

    [Test]
    public async Task ACheckpointInFlightAsTheActorUnloadsIsNotItsFinalSave()
    {
        // The checkpoint holds 3; the actor takes another deposit and unloads while that write is in flight: its final
        // save must still write 7 (not end with the checkpoint's older snapshot).
        var store = new GatedStore(_stores.Create());
        var node = await StartWith(store);
        store.Hold();
        Assert.That(await node.Ask(new BankId(1), new Deposit(3, false)), Is.EqualTo(3));
        await store.Started; // the checkpoint (SaveEvery 100 ms) is held
        Assert.That(await node.Ask(new BankId(1), new Deposit(4, false)), Is.EqualTo(7));

        var stop = Stop(node);
        await Task.Delay(300);
        Assert.That(stop.IsCompleted, Is.False, "the unload waits for the write in flight");
        store.Release();
        await stop;

        var after = await StartWith(store, "after");
        Assert.That(await after.Ask(new BankId(1), new Balance()), Is.EqualTo(7), "the unload wrote what changed after the checkpoint");
    }

    [Test]
    public async Task ATurnThatAskedForASaveAndFailedWritesNothing([Values] bool async)
    {
        // ErrorAction.Reload drops the actor without saving (ADR-066): its half-changed state must not reach the store,
        // although the failed turn added SaveNow.
        var store = new GatedStore(_stores.Create());
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(2000), new Balance()), Is.Zero);
        Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new BankId(2000), new Overdraw(5, async)));
        Assert.That(await node.Ask(new BankId(2000), new Balance()), Is.Zero, "reloaded from the store");
        await Task.Delay(300); // a few wakes: a write would have reached the store
        Assert.That(store.Writes, Is.Zero, "the failed turn's state was not written");
    }

    [Test]
    public async Task AReloadWaitsForTheWriteInFlight()
    {
        // A critical write of 5 is in flight when a failed turn reloads the actor: the reload must read 5 after it, not
        // the record before it (its next write would be refused as stale and the actor fenced).
        var store = new GatedStore(_stores.Create());
        var node = await StartWith(store);
        store.Hold();
        Assert.That(await node.Ask(new BankId(2001), new Deposit(5, Critical: true)), Is.EqualTo(5));
        await store.Started;
        Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new BankId(2001), new Overdraw(1, false)));
        var deposit = node.Ask(new BankId(2001), new Deposit(2, Critical: true)).AsTask();
        store.Release();
        Assert.That(await deposit, Is.EqualTo(7), "the reload read the write in flight");
        await Eventually(() => store.Writes == 2, "the second write was taken");
        var after = await StartWith(store, "after");
        await Stop(node);
        Assert.That(await after.Ask(new BankId(2001), new Balance()), Is.EqualTo(7), "and accepted");
    }

    [Test]
    public async Task AnActorAskedToLeaveIsFencedWhenItsNodeIsDeclaredDead()
    {
        // The node stops while the actor is in a long turn: it is asked to leave once the turn ends. Before that, the
        // cluster declares the node dead (its keys have new owners): the copy must go without OnUnload or a write, not
        // unload as planned and write over what the new owner holds.
        var store = new GatedStore(_stores.Create());
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(170), new Deposit(5, false)), Is.EqualTo(5)); // written on unload only
        TurnGate.Current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = node.Ask(new BankId(170), new HoldTurn()).AsTask();
        await Task.Delay(100);
        foreach (var shard in node.Shards) shard.RequestStop();
        await Task.Delay(100); // asked to leave, its turn still running
        foreach (var shard in node.Shards) shard.RequestUnloadAll();
        await Task.Delay(100);
        TurnGate.Current.TrySetResult();
        await held;
        await Task.WhenAll(node.Shards.Select(static s => s.Stopped)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(store.Writes, Is.Zero, "the fenced copy wrote nothing");
    }

    [Test]
    public async Task ATurnThatFailsAfterAnUnloadWasAskedForIsNotSaved()
    {
        // Keys from 2,000 reload after a failed turn: their half-changed state is dropped unsaved, also when an unload
        // (here an operator's) was asked for while the turn awaited.
        var store = new GatedStore(_stores.Create());
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(2100), new Deposit(5, false)), Is.EqualTo(5)); // written on unload only
        TurnGate.Current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = node.Ask(new BankId(2100), new FailAfterGate(3)).AsTask();
        await Task.Delay(100);
        Assert.That(node.UnloadActors("Bank"), Is.True);
        await Task.Delay(100);
        TurnGate.Current.TrySetResult();
        Assert.ThrowsAsync<RpcException>(async () => await failing);
        await Eventually(() => node.ActorCount == 0, "the failed copy went");
        Assert.That(store.Writes, Is.Zero, "nothing of it was written");
    }

    [Test]
    public async Task ANewOwnerClaimsTheRecordSoTheStaleOneCannotWriteOverIt()
    {
        // A split: a and b each think they own K (two clusters on one store). b loads K after a; a then writes. b's load
        // claimed the record, so a's write is refused (a is fenced), and b's later writes are taken: the store ends with
        // b's state, not with b fenced and its changes lost.
        var store = _stores.Create();
        var a = await Start("a", configure: o => o.ActorStore = store);
        var b = await Start("b", configure: o => { o.ActorStore = store; o.ClusterName = "other"; o.Seeds = []; });
        Assert.That(await a.Ask(new BankId(180), new Deposit(5, Critical: true)), Is.EqualTo(5));
        await Eventually(async () => (await Stores.Peek(store, Key(180))).Data.Length > 0, "a's 5 written");
        Assert.That(await b.Ask(new BankId(180), new Balance()), Is.EqualTo(5), "b loads K");
        Assert.That(await a.Ask(new BankId(180), new Deposit(1, Critical: true)), Is.EqualTo(6)); // its write is refused
        Assert.That(await b.Ask(new BankId(180), new Deposit(2, Critical: true)), Is.EqualTo(7));
        await Eventually(() => a.ActorCount == 0, "a's stale copy was fenced");
        await Task.Delay(300);
        var after = await Start("after", configure: o => { o.ActorStore = store; o.ClusterName = "third"; o.Seeds = []; });
        Assert.That(await after.Ask(new BankId(180), new Balance()), Is.EqualTo(7), "b's state is the stored one");
    }

    private static StoredKey Key(long bank) => new(ActorIds<BankId>.HandlerTypeId, bank);

    /// <summary>Fails its first <see cref="FailWrites"/> writes and <see cref="FailLoads"/> loads (an outage), then works.</summary>
    private sealed class FlakyStore(IActorStore inner) : IActorStore
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public int FailWrites, FailLoads;

        public ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default) =>
            Interlocked.Decrement(ref FailLoads) >= 0 ? ValueTask.FromException(new IOException("store unavailable")) : inner.ClaimAsync(keys, records, ct);

        public ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default) =>
            Interlocked.Decrement(ref FailWrites) >= 0 ? ValueTask.FromException(new IOException("store unavailable")) : inner.SaveAsync(writes, accepted, ct);
    }

    [Test]
    public async Task AFailedCriticalSaveIsRetriedSoon()
    {
        // SaveNow asked for a write now; the store failed it once. The retry comes after the retry delay (a second), not
        // at the next checkpoint (none for SaveEvery zero: only at the unload).
        var inner = _stores.Create();
        var store = new FlakyStore(inner) { FailWrites = 1 };
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(150), new Deposit(9, Critical: true)), Is.EqualTo(9));
        await Eventually(async () => (await Stores.Peek(inner, Key(150))).Data.Length > 0, "written after the retry delay", 3000);
    }

    /// <summary>Holds its first load until released; records the largest batch it was given.</summary>
    private sealed class HeldLoads(IActorStore inner) : IActorStore
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Largest, Loads;

        public async ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref Loads) == 1) await Release.Task;
            InterlockedMax(ref Largest, keys.Length);
            await inner.ClaimAsync(keys, records, ct);
        }

        public ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default) =>
            inner.SaveAsync(writes, accepted, ct);

        private static void InterlockedMax(ref int target, int value)
        {
            for (int seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
                if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    [Test]
    public async Task LoadsWaitingForASlowStoreGoInBoundedBatches()
    {
        // The store holds the first load while 3,000 actors are asked for: they reach it in batches of at most 512.
        var store = new HeldLoads(_stores.Create());
        var node = await StartWith(store);
        var first = node.Ask(new BankId(9000), new Balance()).AsTask();
        for (long key = 9001; key < 12_001; key++) node.Send(new BankId(key), new Deposit(1, false));
        await Task.Delay(300);
        store.Release.TrySetResult();
        await first;
        await Eventually(async () => await node.Ask(new BankId(12_000), new Balance()) == 1, "every deposit was made");
        Assert.That(store.Largest, Is.LessThanOrEqualTo(512));
    }

    /// <summary>Its first <see cref="HangLoads"/> loads and <see cref="HangWrites"/> writes never finish until cancelled (a dead connection).</summary>
    private sealed class HangingStore(IActorStore inner) : IActorStore
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public int HangLoads, HangWrites;

        public async ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref HangLoads) >= 0) await Task.Delay(Timeout.Infinite, ct);
            await inner.ClaimAsync(keys, records, ct);
        }

        public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref HangWrites) >= 0) await Task.Delay(Timeout.Infinite, ct);
            await inner.SaveAsync(writes, accepted, ct);
        }
    }

    [Test]
    public async Task AHungLoadIsCancelledAfterTheStoreTimeoutAndRetried()
    {
        // The store never answers the first load: cancelled after the store timeout, the load is tried again, instead of
        // the shard's loads waiting for good (and every caller until its call timeout).
        var store = new HangingStore(_stores.Create()) { HangLoads = 1 };
        var node = await Start("single", configure: o => (o.ActorStore, o.StoreTimeout) = (store, TimeSpan.FromMilliseconds(300)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.That(await node.Ask(new BankId(170), new Deposit(4, false)), Is.EqualTo(4));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.5)), "not waited for until the call timeout");
    }

    [Test]
    public async Task AHungWriteIsCancelledAfterTheStoreTimeoutAndRetried()
    {
        var inner = _stores.Create();
        var store = new HangingStore(inner) { HangWrites = 1 };
        var node = await Start("single", configure: o => (o.ActorStore, o.StoreTimeout) = (store, TimeSpan.FromMilliseconds(300)));
        Assert.That(await node.Ask(new BankId(171), new Deposit(6, Critical: true)), Is.EqualTo(6));
        await Eventually(async () => (await Stores.Peek(inner, Key(171))).Data.Length > 0, "written after the hung write was cancelled", 3000);
    }

    [Test]
    public async Task ALoadThatFailsIsRetriedWithinTheCallTimeout()
    {
        // The store fails two loads (a restart, a failover): the actor's mail waits and the load is tried again, instead
        // of every caller in the batch getting Error at once.
        var store = new FlakyStore(_stores.Create()) { FailLoads = 2 };
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(160), new Deposit(4, false)), Is.EqualTo(4));
    }
}
