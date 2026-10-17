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

        public ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default) =>
            inner.LoadAsync(keys, records, ct);

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
}
