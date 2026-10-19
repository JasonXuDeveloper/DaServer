using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>
/// Actor records (N5, ADR-068): [Persist] components outlive unloads, stops and crashes; stale owners are fenced. Runs
/// against every store (<see cref="Stores"/>).
/// </summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class PersistenceTests(string kind) : ClusterTest
{
    private readonly Stores _stores = new(kind);

    [OneTimeTearDown]
    public Task DropStores() => _stores.DisposeAsync().AsTask();

    /// <summary>Counts the writes that reach a store.</summary>
    private sealed class CountingStore(IActorStore inner) : IActorStore
    {
        public readonly IActorStore Inner = inner;

        public ValueTask InitializeAsync(CancellationToken ct = default) => Inner.InitializeAsync(ct);
        public int Writes;

        public ValueTask ClaimAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default) =>
            Inner.ClaimAsync(keys, records, ct);

        public ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken ct = default)
        {
            Interlocked.Add(ref Writes, writes.Length);
            return Inner.SaveAsync(writes, accepted, ct);
        }
    }

    private Task<Node> StartWith(IActorStore store, string name = "single", string cluster = "daserver") =>
        Start(name, configure: o =>
        {
            o.ActorStore = store;
            o.ClusterName = cluster;
        });

    /// <summary>A feature whose drain and stop throw (a feature is user code too).</summary>
    private sealed class FailingFeature : NodeFeature
    {
        protected internal override ValueTask DrainAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("drain failed");

        protected internal override void Stop(bool crashed) => throw new InvalidOperationException("stop failed");
    }

    [Test]
    public async Task AFeatureThatFailsToDrainDoesNotKeepTheActorsFromSaving()
    {
        Faults.Allow("FeatureFailed"); // what the test provokes
        var store = new CountingStore(_stores.Create());
        var first = await Start("single", configure: o =>
        {
            o.ActorStore = store;
            o.Features.Add(new FailingFeature());
        });
        Assert.That(await first.Ask(new BankId(110), new Deposit(5, false)), Is.EqualTo(5));
        await Stop(first);
        Assert.That(Faults.Count("FeatureFailed"), Is.EqualTo(2), "its drain and its stop failed");
        Assert.That(store.Writes, Is.EqualTo(1), "the actor still unloaded and wrote its record");

        var second = await StartWith(store);
        Assert.That(await second.Ask(new BankId(110), new Balance()), Is.EqualTo(5));
    }

    [Test]
    public async Task StateSurvivesAGracefulStop()
    {
        var store = new CountingStore(_stores.Create());
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(100), new Deposit(5, false)), Is.EqualTo(5));
        Assert.That(await first.Ask(new BankId(100), new Deposit(2, false)), Is.EqualTo(7));
        Assert.That(store.Writes, Is.Zero, "SaveEvery zero: nothing written before the unload");
        await Stop(first);
        Assert.That(store.Writes, Is.EqualTo(1), "the unload wrote the record once");
        Assert.That(first.StoppedClean, Is.True, "every actor was saved within the budget");

        var second = await StartWith(store);
        Assert.That(await second.Ask(new BankId(100), new Balance()), Is.EqualTo(7), "the ledger came back");
        Assert.That(await second.Ask(new BankId(100), new DepositCount()), Is.EqualTo(2), "so did the second component");
    }

    [Test]
    public async Task CheckpointsSurviveACrashAndUnchangedRecordsAreNotWritten()
    {
        var store = new CountingStore(_stores.Create());
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(1), new Deposit(3, false)), Is.EqualTo(3));
        await Eventually(() => store.Writes == 1, "the change was checkpointed (SaveEvery 100 ms)");
        await Task.Delay(500); // several more checkpoints of an unchanged record
        Assert.That(store.Writes, Is.EqualTo(1), "an unchanged record is not written again");

        await first.KillAsync();
        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new BankId(1), new Balance()), Is.EqualTo(3), "the checkpoint survived the crash");
    }

    [Test]
    public async Task SaveNowWritesACriticalChangeAtOnce()
    {
        var store = new CountingStore(_stores.Create());
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(101), new Deposit(9, Critical: true)), Is.EqualTo(9));
        await Eventually(() => store.Writes == 1, "SaveNow wrote without waiting for an unload");
        await first.KillAsync();

        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new BankId(101), new Balance()), Is.EqualTo(9));
    }

    [Test]
    public async Task RecordsHoldWholeTurnsOnly()
    {
        var store = new CountingStore(_stores.Create());
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(103), new DepositTwice(5, new BankId(104))), Is.EqualTo(10));
        await Eventually(() => store.Writes == 1, "SaveNow wrote once the turn ended");
        await first.KillAsync();

        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new BankId(103), new Balance()), Is.EqualTo(10), "not the state halfway through the turn");
    }

    [Test]
    public async Task AStopUnderLoadLosesNoAcknowledgedDeposit()
    {
        // Callers deposit through b while a stops (its actors hand over to b), then b stops last and writes everything:
        // every acknowledged deposit is in the record, and nothing that was not attempted is. A failed call may or may
        // not have been applied (its reply was lost), so it bounds the balance from above only.
        var store = new CountingStore(_stores.Create());
        var a = await StartWith(store, "a");
        var b = await StartWith(store, "b");
        await Converged();
        var load = Load(b, first: 100, keys: 32, critical: false);
        await Task.Delay(200);
        await Stop(a);
        await Task.Delay(200);
        var (acknowledged, failed) = await load.Stop();
        Assert.That(a.StoppedClean, Is.True, "a handed over or saved every actor within its budget");

        var balances = new int[acknowledged.Length];
        for (int i = 0; i < balances.Length; i++)
        {
            balances[i] = await b.Ask(new BankId(100 + i), new Balance());
            Assert.That(balances[i], Is.InRange(acknowledged[i], acknowledged[i] + failed[i]), $"key {100 + i} on b");
        }

        await Stop(b);
        Assert.That(b.StoppedClean, Is.True);
        var c = await StartWith(store, "c");
        for (int i = 0; i < balances.Length; i++)
            Assert.That(await c.Ask(new BankId(100 + i), new Balance()), Is.EqualTo(balances[i]), $"key {100 + i} stored");
        Assert.That(acknowledged.Sum(), Is.GreaterThan(100), "the stop happened under load");
    }

    [Test]
    public async Task AKillLosesAtMostTheLastCheckpointInterval()
    {
        // Keys below 100 checkpoint every 100 ms. What was acknowledged half a second (several checkpoints) before the
        // kill survives it; nothing survives that was not attempted, and no record holds half a turn.
        var store = new CountingStore(_stores.Create());
        var first = await StartWith(store);
        var load = Load(first, first: 1, keys: 16, critical: false);
        await Task.Delay(300);
        int[] earlier = load.Snapshot();
        await Task.Delay(500);
        await first.KillAsync();
        var (acknowledged, failed) = await load.Stop();

        var second = await StartWith(store, "after");
        for (int i = 0; i < earlier.Length; i++)
            Assert.That(await second.Ask(new BankId(1 + i), new Balance()), Is.InRange(earlier[i], acknowledged[i] + failed[i]),
                $"key {1 + i}: at least what was acknowledged one checkpoint interval and more before the kill");
        Assert.That(earlier.Sum(), Is.GreaterThan(100), "the kill happened under load");
    }

    /// <summary>One caller per key depositing 1 gold at a time until stopped; counts acknowledged and failed calls.</summary>
    private static DepositLoad Load(Node node, int first, int keys, bool critical)
    {
        var load = new DepositLoad(keys);
        for (int i = 0; i < keys; i++)
        {
            int index = i;
            load.Callers[i] = Task.Run(async () =>
            {
                while (!load.Stopping)
                {
                    try
                    {
                        await node.Ask(new BankId(first + index), new Deposit(1, critical)).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                        Interlocked.Increment(ref load.Acknowledged[index]);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref load.Failed[index]);
                        await Task.Delay(10);
                    }
                }
            });
        }

        return load;
    }

    private sealed class DepositLoad(int keys)
    {
        public readonly Task[] Callers = new Task[keys];
        public readonly int[] Acknowledged = new int[keys], Failed = new int[keys];
        public volatile bool Stopping;

        public int[] Snapshot()
        {
            var counts = new int[Acknowledged.Length];
            for (int i = 0; i < counts.Length; i++) counts[i] = Volatile.Read(ref Acknowledged[i]);
            return counts;
        }

        public async Task<(int[] Acknowledged, int[] Failed)> Stop()
        {
            Stopping = true;
            await Task.WhenAll(Callers);
            return (Acknowledged, Failed);
        }
    }

    [Test]
    public async Task AStaleOwnerIsFencedByTheRecordsVersion()
    {
        // Two nodes that cannot see each other (as in a split) both load the actor from one store.
        Faults.Allow("LinkRefused"); // b's seed is a, which refuses another cluster's link
        var store = new CountingStore(_stores.Create());
        var a = await StartWith(store, "a", cluster: "left");
        var b = await StartWith(store, "b", cluster: "right");
        Assert.That(await b.Ask(new BankId(102), new Balance()), Is.Zero, "b holds the actor at version 0");
        Assert.That(await a.Ask(new BankId(102), new Deposit(1, Critical: true)), Is.EqualTo(1));
        await Eventually(() => store.Writes == 1, "a wrote version 1");
        Assert.That(await b.Ask(new BankId(102), new Deposit(10, Critical: true)), Is.EqualTo(10), "b's copy is stale");
        await Eventually(() => a.ActorCount + b.ActorCount == 1, "the later writer was refused and dropped its copy");
        Assert.That(await b.Ask(new BankId(102), new Balance()) + await a.Ask(new BankId(102), new Balance()), Is.EqualTo(2),
            "both serve the stored record again: one deposit was refused, the other node reloaded it");
    }
}
