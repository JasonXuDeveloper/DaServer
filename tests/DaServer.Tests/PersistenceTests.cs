namespace DaServer.Tests;

/// <summary>
/// Actor records (N5, ADR-068): [Persist] components outlive unloads, stops and crashes; stale owners are fenced. Runs
/// against every store (<see cref="Stores"/>).
/// </summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class PersistenceTests(string kind) : StoreTest(kind)
{
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
        var store = NewStore();
        var first = await StartWith(store, configure: static o => o.Features.Add(new FailingFeature()));
        Assert.That(await first.Ask(new BankId(110), new Deposit(5, false)), Is.EqualTo(5));
        await Stop(first);
        Assert.That(Faults.Count("FeatureFailed"), Is.EqualTo(2), "its drain and its stop failed");
        Assert.That(store.Writes, Is.EqualTo(1), "the actor still unloaded and wrote its record");

        var second = await StartWith(store);
        Assert.That(await second.Ask(new BankId(110), new Balance()), Is.EqualTo(5));
    }

    /// <summary>A feature whose drain never ends and ignores its token.</summary>
    private sealed class StuckFeature : NodeFeature
    {
        protected internal override ValueTask DrainAsync(CancellationToken cancellationToken) => new(new TaskCompletionSource().Task);
    }

    [Test]
    public async Task AFeatureThatNeverDrainsDoesNotKeepTheActorsFromSaving()
    {
        var store = NewStore();
        var first = await StartWith(store, configure: static o =>
        {
            o.ShutdownTimeout = TimeSpan.FromSeconds(2);
            o.Features.Add(new StuckFeature());
        });
        Assert.That(await first.Ask(new BankId(111), new Deposit(5, false)), Is.EqualTo(5));
        Faults.Allow("BudgetExceeded"); // the drain takes the whole budget: the unload may report what it had no time for
        await Stop(first);
        Assert.That(store.Writes, Is.EqualTo(1), "the actor still unloaded and wrote its record");
    }

    [Test]
    public async Task StateSurvivesAGracefulStop()
    {
        var store = NewStore();
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
        var store = NewStore();
        var first = await StartWith(store);
        Assert.That(await first.Ask(new CheckpointedBankId(1), new Deposit(3, false)), Is.EqualTo(3));
        await Eventually(() => store.Writes == 1, "the change was checkpointed (SaveEvery 1 s)");
        await Task.Delay(2500); // more checkpoints of an unchanged record
        Assert.That(store.Writes, Is.EqualTo(1), "an unchanged record is not written again");

        await first.KillAsync();
        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new CheckpointedBankId(1), new Balance()), Is.EqualTo(3), "the checkpoint survived the crash");
    }

    [Test]
    public async Task SaveNowWritesACriticalChangeAtOnce()
    {
        var store = NewStore();
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(101), new Deposit(9, Critical: true)), Is.EqualTo(9));
        await Eventually(() => store.Writes == 1, "SaveNow wrote without waiting for an unload");
        await first.KillAsync();

        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new BankId(101), new Balance()), Is.EqualTo(9));
    }

    [Test]
    public async Task RecordsHoldTheStateAfterWholeMessages()
    {
        // A change in two messages (the request, then the answer to its ask), each asking for a write: a record holds
        // the state after a whole message, and the last one is written.
        var store = NewStore();
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(103), new DepositTwice(5, new BankId(104))), Is.EqualTo(10));
        await Eventually(() => Volatile.Read(ref store.Writes) >= 1, "SaveNow wrote without waiting for an unload");
        await Task.Delay(300); // a write still asked for would have reached the store
        Assert.That(store.Writes, Is.InRange(1, 2), "one write per message at most");
        await first.KillAsync();

        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new BankId(103), new Balance()), Is.EqualTo(10), "the state after the last message");
    }

    [Test]
    public async Task AStopUnderLoadLosesNoAcknowledgedDeposit()
    {
        // Callers deposit through b while a stops (its actors hand over to b), then b stops last and writes everything:
        // every acknowledged deposit is in the record, and nothing that was not attempted is. A failed call may or may
        // not have been applied (its reply was lost), so it bounds the balance from above only.
        var store = NewStore();
        var a = await StartWith(store, "a");
        var b = await StartWith(store, "b");
        await Converged();
        var load = Load(b, keys: 32, static (node, key) => node.Ask(new BankId(100 + key), new Deposit(1, false)));
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
        // Checkpointed banks write every second. What was acknowledged one and a half seconds (a checkpoint and more)
        // before the kill survives it; nothing survives that was not attempted.
        var store = NewStore();
        var first = await StartWith(store);
        var load = Load(first, keys: 16, static (node, key) => node.Ask(new CheckpointedBankId(1 + key), new Deposit(1, false)));
        await Task.Delay(300);
        int[] earlier = load.Snapshot();
        await Task.Delay(1500);
        await first.KillAsync();
        var (acknowledged, failed) = await load.Stop();

        var second = await StartWith(store, "after");
        for (int i = 0; i < earlier.Length; i++)
            Assert.That(await second.Ask(new CheckpointedBankId(1 + i), new Balance()), Is.InRange(earlier[i], acknowledged[i] + failed[i]),
                $"key {1 + i}: at least what was acknowledged one checkpoint interval and more before the kill");
        Assert.That(earlier.Sum(), Is.GreaterThan(100), "the kill happened under load");
    }

    /// <summary>One caller per key depositing 1 gold at a time until stopped; counts acknowledged and failed calls.</summary>
    private static DepositLoad Load(Node node, int keys, Func<Node, int, Reply<int>> deposit)
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
                        await deposit(node, index).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
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
        var store = NewStore();
        var a = await StartWith(store, "a", static o => o.ClusterName = "left");
        var b = await StartWith(store, "b", static o => o.ClusterName = "right");
        Assert.That(await b.Ask(new BankId(102), new Balance()), Is.Zero, "b holds the actor at version 0");
        Assert.That(await a.Ask(new BankId(102), new Deposit(1, Critical: true)), Is.EqualTo(1));
        await Eventually(() => store.Writes == 1, "a wrote version 1");
        Assert.That(await b.Ask(new BankId(102), new Deposit(10, Critical: true)), Is.EqualTo(10), "b's copy is stale");
        await Eventually(() => a.ActorCount + b.ActorCount == 1, "the later writer was refused and dropped its copy");
        Assert.That(await b.Ask(new BankId(102), new Balance()) + await a.Ask(new BankId(102), new Balance()), Is.EqualTo(2),
            "both serve the stored record again: one deposit was refused, the other node reloaded it");
    }
}
