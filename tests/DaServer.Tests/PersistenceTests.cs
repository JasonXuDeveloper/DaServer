using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>Actor records (N5, ADR-068): [Persist] components outlive unloads, stops and crashes; stale owners are fenced.</summary>
public class PersistenceTests : ClusterTest
{
    /// <summary>Counts the writes that reach a store.</summary>
    private sealed class CountingStore : IActorStore
    {
        public readonly InMemoryActorStore Inner = new();
        public int Writes;

        public ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken ct = default) =>
            Inner.LoadAsync(keys, records, ct);

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

    [Test]
    public async Task StateSurvivesAGracefulStop()
    {
        var store = new CountingStore();
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(100), new Deposit(5, false)), Is.EqualTo(5));
        Assert.That(await first.Ask(new BankId(100), new Deposit(2, false)), Is.EqualTo(7));
        Assert.That(store.Writes, Is.Zero, "SaveEvery zero: nothing written before the unload");
        await Stop(first);
        Assert.That(store.Writes, Is.EqualTo(1), "the unload wrote the record once");

        var second = await StartWith(store);
        Assert.That(await second.Ask(new BankId(100), new Balance()), Is.EqualTo(7), "the ledger came back");
        Assert.That(await second.Ask(new BankId(100), new DepositCount()), Is.EqualTo(2), "so did the second component");
    }

    [Test]
    public async Task CheckpointsSurviveACrashAndUnchangedRecordsAreNotWritten()
    {
        var store = new CountingStore();
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
        var store = new CountingStore();
        var first = await StartWith(store);
        Assert.That(await first.Ask(new BankId(101), new Deposit(9, Critical: true)), Is.EqualTo(9));
        await Eventually(() => store.Writes == 1, "SaveNow wrote without waiting for an unload");
        await first.KillAsync();

        var second = await StartWith(store, "after");
        Assert.That(await second.Ask(new BankId(101), new Balance()), Is.EqualTo(9));
    }

    [Test]
    public async Task AStaleOwnerIsFencedByTheRecordsVersion()
    {
        // Two nodes that cannot see each other (as in a split) both load the actor from one store.
        var store = new InMemoryActorStore();
        var a = await StartWith(store, "a", cluster: "left");
        var b = await StartWith(store, "b", cluster: "right");
        Assert.That(await b.Ask(new BankId(102), new Balance()), Is.Zero, "b holds the actor at version 0");
        Assert.That(await a.Ask(new BankId(102), new Deposit(1, Critical: true)), Is.EqualTo(1));
        await Eventually(() => store.Count == 1, "a wrote version 1");
        Assert.That(await b.Ask(new BankId(102), new Deposit(10, Critical: true)), Is.EqualTo(10), "b's copy is stale");
        await Eventually(() => a.ActorCount + b.ActorCount == 1, "the later writer was refused and dropped its copy");
        Assert.That(await b.Ask(new BankId(102), new Balance()) + await a.Ask(new BankId(102), new Balance()), Is.EqualTo(2),
            "both serve the stored record again: one deposit was refused, the other node reloaded it");
    }
}
