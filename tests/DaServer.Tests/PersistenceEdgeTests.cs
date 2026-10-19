using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>
/// Actor records (ADR-068) at the edges: a write in flight while the actor unloads, fails or reloads. A gate holds the
/// store's writes, so each interleaving is forced instead of left to a machine's speed.
/// </summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class PersistenceEdgeTests(string kind) : StoreTest(kind)
{
    [Test]
    public async Task AWriteWhoseAnswerWasLostDoesNotFenceItsOwner()
    {
        // The critical write of 5 lands but fails; 6 more is acknowledged meanwhile; the retry (from the same version)
        // names the lost write, so the store writes 11 after it instead of refusing the owner as a stale one.
        Faults.Allow("SaveFailed"); // what the test provokes
        var store = NewStore();
        store.LoseAnswers = 1;
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(500), new Deposit(5, Critical: true)), Is.EqualTo(5));
        await Eventually(() => Volatile.Read(ref store.Answered) >= 1, "the first write landed and failed");
        Assert.That(await node.Ask(new BankId(500), new Deposit(6, Critical: true)), Is.EqualTo(11));
        await Eventually(() => Volatile.Read(ref store.Answered) >= 2, "the retry was answered");
        Assert.That(await node.Ask(new BankId(500), new Balance()), Is.EqualTo(11), "the owner was not fenced");
        await node.KillAsync();

        var again = await StartWith(store, "again");
        Assert.That(await again.Ask(new BankId(500), new Balance()), Is.EqualTo(11), "the acknowledged deposit was written");
    }

    [Test]
    public async Task AStateThatChangesBackAfterALostAnswerIsWrittenAgain()
    {
        // 9 lands but its answer is lost; the state goes back to the acknowledged 5 before the retry: the store holds 9,
        // so 5 is written although it is what the owner last saw acknowledged.
        Faults.Allow("SaveFailed"); // what the test provokes
        var store = NewStore();
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(501), new SetGold(5)), Is.EqualTo(5));
        await Eventually(() => Volatile.Read(ref store.Answered) >= 1, "5 was written");
        store.LoseAnswers = 1;
        Assert.That(await node.Ask(new BankId(501), new SetGold(9)), Is.EqualTo(9));
        await Eventually(() => Volatile.Read(ref store.Answered) >= 2, "9 landed and failed");
        Assert.That(await node.Ask(new BankId(501), new SetGold(5)), Is.EqualTo(5));
        await Stop(node);

        var again = await StartWith(store, "again");
        Assert.That(await again.Ask(new BankId(501), new Balance()), Is.EqualTo(5), "the acknowledged change back was written");
    }

    [Test]
    public async Task ARecordThatDoesNotSerializeHoldsNoOtherWrite()
    {
        // One actor's state stops serializing (a cycle) and it asks for a critical write, then the node stops: that
        // write is given up (the store keeps its last record), and every other actor's writes and the stop go on.
        Faults.Allow("RecordUnwritable"); // what the test provokes
        var store = NewStore();
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
        var store = NewStore();
        var node = await StartWith(store);
        store.WriteGate.Hold();
        Assert.That(await node.Ask(new CheckpointedBankId(1), new Deposit(3, false)), Is.EqualTo(3));
        await store.WriteGate.Started; // the checkpoint (SaveEvery 1 s) is held
        Assert.That(await node.Ask(new CheckpointedBankId(1), new Deposit(4, false)), Is.EqualTo(7));

        var stop = Stop(node);
        await Task.Delay(300);
        Assert.That(stop.IsCompleted, Is.False, "the unload waits for the write in flight");
        store.WriteGate.Release();
        await stop;

        var after = await StartWith(store, "after");
        Assert.That(await after.Ask(new CheckpointedBankId(1), new Balance()), Is.EqualTo(7), "the unload wrote what changed after the checkpoint");
    }

    [Test]
    public async Task AHandlerThatAskedForASaveAndFailedWritesNothing([Values] bool async)
    {
        // ErrorAction.Reload drops the actor without saving (ADR-066): its half-changed state must not reach the store,
        // although the failed handler (async: the handler of an answer) added SaveNow.
        var store = NewStore();
        var node = await StartWith(store);
        Assert.That(await node.Ask(new ReloadingBankId(2000), new Balance()), Is.Zero);
        var error = Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new ReloadingBankId(2000), new Overdraw(5, async)))!;
        Assert.That(error.Status, Is.EqualTo(RpcStatus.Error), "the failure is the caller's reply");
        Assert.That(await node.Ask(new ReloadingBankId(2000), new Balance()), Is.Zero, "reloaded from the store");
        await Task.Delay(300); // a few wakes: a write would have reached the store
        Assert.That(store.Writes, Is.Zero, "the failed handler's state was not written");
    }

    [Test]
    public async Task AReloadWaitsForTheWriteInFlight()
    {
        // A critical write of 5 is in flight when a failed handler reloads the actor: the reload must read 5 after it,
        // not the record before it (its next write would be refused as stale and the actor fenced).
        var store = NewStore();
        var node = await StartWith(store);
        store.WriteGate.Hold();
        Assert.That(await node.Ask(new ReloadingBankId(2001), new Deposit(5, Critical: true)), Is.EqualTo(5));
        await store.WriteGate.Started;
        Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new ReloadingBankId(2001), new Overdraw(1, false)));
        var deposit = node.Ask(new ReloadingBankId(2001), new Deposit(2, Critical: true)).AsTask();
        store.WriteGate.Release();
        Assert.That(await deposit, Is.EqualTo(7), "the reload read the write in flight");
        await Eventually(() => store.Writes == 2, "the second write was taken");
        var after = await StartWith(store, "after");
        await Stop(node);
        Assert.That(await after.Ask(new ReloadingBankId(2001), new Balance()), Is.EqualTo(7), "and accepted");
    }

    [Test]
    public async Task AnActorAskedToLeaveIsFencedWhenItsNodeIsDeclaredDead()
    {
        // The node stops while its shards are busy (held): the actor is asked to leave once they run again. Before that,
        // the cluster declares the node dead (its keys have new owners): the copy must go without its Unloading or a
        // write, not unload as planned and write over what the new owner holds.
        var store = NewStore();
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(170), new Deposit(5, false)), Is.EqualTo(5)); // written on unload only
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        HoldShards(node, gate);
        await Task.Delay(100);
        foreach (var shard in node.Shards) shard.RequestStop();
        await Task.Delay(100); // asked to leave, its shard still busy
        foreach (var shard in node.Shards) shard.RequestUnloadAll();
        await Task.Delay(100);
        gate.Set();
        await Task.WhenAll(node.Shards.Select(static s => s.Stopped)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(store.Writes, Is.Zero, "the fenced copy wrote nothing");
    }

    [Test]
    public async Task ANewOwnerClaimsTheRecordSoTheStaleOneCannotWriteOverIt()
    {
        // A split: a and b each think they own K (two clusters on one store). b loads K after a; a then writes. b's load
        // claimed the record, so a's write is refused (a is fenced), and b's later writes are taken: the store ends with
        // b's state, not with b fenced and its changes lost.
        var store = Stores.Create();
        var a = await StartWith(store, "a");
        var b = await StartWith(store, "b", static o => { o.ClusterName = "other"; o.Seeds = []; });
        Assert.That(await a.Ask(new BankId(180), new Deposit(5, Critical: true)), Is.EqualTo(5));
        await Eventually(async () => (await Stores.Peek(store, Key(180))).Data.Length > 0, "a's 5 written");
        Assert.That(await b.Ask(new BankId(180), new Balance()), Is.EqualTo(5), "b loads K");
        Assert.That(await a.Ask(new BankId(180), new Deposit(1, Critical: true)), Is.EqualTo(6)); // its write is refused
        Assert.That(await b.Ask(new BankId(180), new Deposit(2, Critical: true)), Is.EqualTo(7));
        await Eventually(() => a.ActorCount == 0, "a's stale copy was fenced");
        await Task.Delay(300);
        var after = await StartWith(store, "after", static o => { o.ClusterName = "third"; o.Seeds = []; });
        Assert.That(await after.Ask(new BankId(180), new Balance()), Is.EqualTo(7), "b's state is the stored one");
    }

    private static StoredKey Key(long bank) => new(ActorIds<BankId>.HandlerTypeId, bank);

    [Test]
    public async Task AFailedCriticalSaveIsRetriedSoon()
    {
        // SaveNow asked for a write now; the store failed it once. The retry comes after the retry delay (a second), not
        // at the next checkpoint (none for SaveEvery zero: only at the unload).
        var store = NewStore();
        store.FailWrites = 1;
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(150), new Deposit(9, Critical: true)), Is.EqualTo(9));
        await Eventually(async () => (await Stores.Peek(store.Inner, Key(150))).Data.Length > 0, "written after the retry delay", 3000);
    }

    [Test]
    public async Task LoadsWaitingForASlowStoreGoInBoundedBatches()
    {
        // The store holds the first load while 3,000 actors are asked for: they reach it in batches of at most 512.
        var store = NewStore();
        store.LoadGate.Hold(calls: 1);
        var node = await StartWith(store);
        var first = node.Ask(new BankId(9000), new Balance()).AsTask();
        for (long key = 9001; key < 12_001; key++) node.Send(new BankId(key), new Deposit(1, false));
        await Task.Delay(300);
        store.LoadGate.Release();
        await first;
        await Eventually(async () => await node.Ask(new BankId(12_000), new Balance()) == 1, "every deposit was made");
        Assert.That(store.LargestLoad, Is.LessThanOrEqualTo(512));
    }

    [Test]
    public async Task AHungLoadIsCancelledAfterTheStoreTimeoutAndRetried()
    {
        // The store never answers the first load: cancelled after the store timeout, the load is tried again, instead of
        // the shard's loads waiting for good (and every caller until its call timeout).
        var store = NewStore();
        store.HangLoads = 1;
        var node = await StartWith(store, configure: static o => o.StoreTimeout = TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.That(await node.Ask(new BankId(170), new Deposit(4, false)), Is.EqualTo(4));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.5)), "not waited for until the call timeout");
    }

    [Test]
    public async Task AHungWriteIsCancelledAfterTheStoreTimeoutAndRetried()
    {
        var store = NewStore();
        store.HangWrites = 1;
        var node = await StartWith(store, configure: static o => o.StoreTimeout = TimeSpan.FromMilliseconds(300));
        Assert.That(await node.Ask(new BankId(171), new Deposit(6, Critical: true)), Is.EqualTo(6));
        await Eventually(async () => (await Stores.Peek(store.Inner, Key(171))).Data.Length > 0, "written after the hung write was cancelled", 3000);
    }

    [Test]
    public async Task ALoadThatFailsIsRetriedWithinTheCallTimeout()
    {
        // The store fails two loads (a restart, a failover): the actor's mail waits and the load is tried again, instead
        // of every caller in the batch getting Error at once.
        var store = NewStore();
        store.FailLoads = 2;
        var node = await StartWith(store);
        Assert.That(await node.Ask(new BankId(160), new Deposit(4, false)), Is.EqualTo(4));
    }
}
