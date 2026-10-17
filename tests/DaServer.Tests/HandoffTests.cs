using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>
/// Actors that change node take their record along (N6, ADR-072): no store read or write on scaling out or in,
/// [Handoff] components move without being stored, and without a store state still moves. Runs against every store.
/// </summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class HandoffTests(string kind) : ClusterTest
{
    private readonly Stores _stores = new(kind);

    [OneTimeTearDown]
    public Task DropStores() => _stores.DisposeAsync().AsTask();

    /// <summary>Counts what reaches a store; a write can be held to look at the actor meanwhile.</summary>
    private sealed class WatchedStore(IActorStore inner) : IActorStore
    {
        public int Loads, Writes;
        public TaskCompletionSource? HoldWrites;
        public readonly TaskCompletionSource WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default)
        {
            Interlocked.Add(ref Loads, keys.Length);
            return inner.LoadAsync(keys, records, ct);
        }

        public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default)
        {
            WriteStarted.TrySetResult();
            if (HoldWrites != null) await HoldWrites.Task;
            Interlocked.Add(ref Writes, writes.Length);
            await inner.SaveAsync(writes, accepted, ct);
        }
    }

    private Task<Node> StartWith(IActorStore? store, string name) => Start(name, configure: o => o.ActorStore = store);

    [Test]
    public async Task ScalingOutAndInMovesRecordsWithoutTheStore()
    {
        var store = new WatchedStore(_stores.Create());
        var a = await StartWith(store, "a");
        for (int key = 100; key < 140; key++)
        {
            Assert.That(await a.Ask(new BankId(key), new Deposit(key, false)), Is.EqualTo(key));
            Assert.That(await a.Ask(new BankId(key), new Hit()), Is.EqualTo(1));
        }

        int loads = store.Loads;
        var b = await StartWith(store, "b");
        await Converged();
        int moved = 0;
        for (int key = 100; key < 140; key++)
        {
            if (a.OwnerOf<BankId>(key) == b.Id) moved++;
            Assert.That(await a.Ask(new BankId(key), new Balance()), Is.EqualTo(key), "the ledger moved");
            Assert.That(await b.Ask(new BankId(key), new Hit()), Is.EqualTo(2), "so did the [Handoff] streak");
        }

        Assert.That(moved, Is.GreaterThan(0), "the new node took some of the keys");
        Assert.That(store.Loads, Is.EqualTo(loads), "the new owner read nothing from the store");
        Assert.That(store.Writes, Is.Zero, "and the previous one wrote nothing (SaveEvery zero)");

        await Stop(b); // scaling in: b's actors go back to a
        for (int key = 100; key < 140; key++)
            Assert.That(await a.Ask(new BankId(key), new Hit()), Is.EqualTo(3),
                $"the streak came back with them (store writes {store.Writes}, loads {store.Loads - loads}: a fallback when not 0)");
        Assert.That(store.Loads, Is.EqualTo(loads), "still nothing read");
        Assert.That(store.Writes, Is.Zero, "and nothing written: b handed everything over");
        Assert.That(b.StoppedClean, Is.True);

        await Stop(a); // the last node: nobody to hand over to, so it writes
        Assert.That(store.Writes, Is.EqualTo(40));
        var c = await StartWith(store, "c");
        Assert.That(await c.Ask(new BankId(120), new Balance()), Is.EqualTo(120), "the ledger is stored");
        Assert.That(await c.Ask(new BankId(120), new Hit()), Is.EqualTo(1), "the streak is not");
    }

    [Test]
    public async Task NoSystemSeesACopyThatHandedItsStateOff()
    {
        var a = await StartWith(_stores.Create(), "a");
        var keys = Enumerable.Range(7000, 40).Select(static k => (long)k).ToArray();
        foreach (long key in keys) Assert.That(await a.Ask(new BankId(key), new Hit()), Is.EqualTo(1));

        // b takes some keys but answers no record: a's copies wait, handed off, until b's shards run again.
        using var gate = new ManualResetEventSlim();
        var b = await StartWith(_stores.Create(), "b");
        b.PostToEveryShard(static (_, g) => g.Wait(TimeSpan.FromSeconds(20)), gate);
        await Converged();
        var moved = keys.Where(k => a.OwnerOf<BankId>(k) == b.Id).ToArray();
        Assert.That(moved, Is.Not.Empty);
        await Task.Delay(500); // a unloaded them and sent their records
        int Ticks() => moved.Sum(k => TallySystem.Ticks.GetValueOrDefault((a.Id, k)));
        int before = Ticks();
        await Task.Delay(300); // 15 ticks at 50 ticks/s
        Assert.That(Ticks(), Is.EqualTo(before), "a's handed-off copies are seen by no system");

        gate.Set();
        foreach (long key in moved) Assert.That(await a.Ask(new BankId(key), new Hit()), Is.EqualTo(2), "the streak moved");
        await Eventually(() => moved.All(k => TallySystem.Ticks.GetValueOrDefault((b.Id, k)) > 0), "b's live copies tick");
    }

    [Test]
    public async Task WithoutAStoreStateStillMoves()
    {
        var a = await StartWith(null, "a");
        for (int key = 200; key < 230; key++) await a.Ask(new BankId(key), new Deposit(key, false));
        var b = await StartWith(null, "b");
        await Converged();
        for (int key = 200; key < 230; key++)
            Assert.That(await b.Ask(new BankId(key), new Balance()), Is.EqualTo(key));
        Assert.That(b.ActorCount, Is.GreaterThan(0));
    }

    [Test]
    public async Task MailForAnActorBeingSavedWaitsForItsRemoval()
    {
        var store = new WatchedStore(_stores.Create());
        var a = await StartWith(store, "a");
        Assert.That(await a.Ask(new BankId(1000), new Deposit(1, false)), Is.EqualTo(1));
        store.HoldWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // idle: unloaded, its record taken
        var deposit = a.Ask(new BankId(1000), new Deposit(1, false)).AsTask();
        await Task.Delay(100);
        Assert.That(deposit.IsCompleted, Is.False, "the unloaded copy does not take mail");
        store.HoldWrites.SetResult();
        Assert.That(await deposit, Is.EqualTo(2), "the mail reloaded the actor with what was written");
    }
}
