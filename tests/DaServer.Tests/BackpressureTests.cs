namespace DaServer.Tests;

/// <summary>Bounded queues: a flood is refused with <see cref="RpcStatus.Overloaded"/> instead of growing memory.</summary>
public class BackpressureTests : ClusterTest
{
    [Test]
    public async Task AFullActorMailboxRefusesNewCalls()
    {
        // The actor's record load is held, so its mail waits: beyond the capacity, new calls are refused.
        var store = new TestStore(new DaServer.Persistence.InMemoryActorStore());
        store.LoadGate.Hold();
        var node = await Start("single", shards: 1, configure: o =>
        {
            o.ActorMailboxCapacity = 4;
            o.ActorStore = store;
        });
        var calls = Enumerable.Range(0, 50).Select(_ => StatusOf(node.Ask(new CounterId(1), new Add(1)))).ToArray();
        await store.LoadGate.Started;
        await Eventually(() => node.Dropped(DropReason.MailboxFull) == 46, "the calls beyond the capacity were refused");
        store.LoadGate.Release();
        var statuses = await Task.WhenAll(calls);

        int ok = statuses.Count(s => s == RpcStatus.Ok), overloaded = statuses.Count(s => s == RpcStatus.Overloaded);
        Assert.That(ok + overloaded, Is.EqualTo(50), "every call either ran or was refused");
        Assert.That(ok, Is.EqualTo(4), "exactly the mailbox capacity was admitted");
        Assert.That(await node.Ask(new CounterId(1), new Get()), Is.EqualTo(ok), "refused calls did not run");
        Assert.That(node.Dropped(DropReason.MailboxFull), Is.EqualTo(overloaded));
        Assert.That(node.Dropped(DropReason.QueueFull), Is.Zero);
    }

    [Test]
    public async Task AnActorTypeSetsItsOwnMailboxCapacity()
    {
        // The node allows 4096 waiting messages per actor; Narrow allows 2 (its record load is held meanwhile).
        var store = new TestStore(new DaServer.Persistence.InMemoryActorStore());
        store.LoadGate.Hold();
        var node = await Start("single", shards: 1, configure: o => o.ActorStore = store);
        var calls = Enumerable.Range(0, 30).Select(_ => StatusOf(node.Ask(new NarrowId(1), new Step()))).ToArray();
        await store.LoadGate.Started;
        await Eventually(() => node.Dropped(DropReason.MailboxFull) == 28, "the calls beyond the type's capacity were refused");
        store.LoadGate.Release();
        var statuses = await Task.WhenAll(calls);
        int ok = statuses.Count(s => s == RpcStatus.Ok), overloaded = statuses.Count(s => s == RpcStatus.Overloaded);
        Assert.That(ok + overloaded, Is.EqualTo(30));
        Assert.That(ok, Is.EqualTo(2), "the actor type's capacity, not the node's");
        Assert.That(node.Dropped(DropReason.MailboxFull), Is.EqualTo(overloaded));
    }

    [Test]
    public async Task AFullShardRefusesNewCallsAndRecovers()
    {
        var node = await Start("single", shards: 1, configure: static o => o.DispatchQueueCapacity = 16);
        var shard = node.Shards[0];
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // the shard is busy until the gate opens
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");

        var calls = Enumerable.Range(0, 100).Select(_ => StatusOf(node.Ask(new CounterId(1), new Add(1)))).ToArray();
        int refused = calls.Count(c => c.IsCompleted);
        Assert.That(refused, Is.EqualTo(100 - 16), "calls beyond the capacity fail fast while the shard is busy");
        Assert.That(calls.Where(c => c.IsCompleted).All(c => c.Result == RpcStatus.Overloaded));

        gate.Set();
        var statuses = await Task.WhenAll(calls);
        Assert.That(statuses.Count(s => s == RpcStatus.Ok), Is.EqualTo(16));
        Assert.That(await node.Ask(new CounterId(1), new Get()), Is.EqualTo(16), "admitted calls ran once the shard caught up");
        Assert.That(node.Dropped(DropReason.QueueFull), Is.EqualTo(84));
    }

    [Test]
    public async Task AShardThreadBlockedLongerThanStallAfterIsReported()
    {
        Faults.Allow("ShardStalled"); // what the test provokes
        var node = await Start("single", shards: 1, configure: static o => o.StallAfter = TimeSpan.FromMilliseconds(200));
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        node.Shards[0].Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // what DAS009 reports in user code
        await Eventually(() => node.StalledShards == 1, "the watchdog reports the blocked shard");
        gate.Set();
        await Eventually(() => node.StalledShards == 0, "and clears it once the shard is back in its loop");
    }

    [Test]
    public async Task AReplyKeptPastTheCallTimeoutLeavesTheActorServing()
    {
        // A request whose reply is kept for later and given after its caller gave up: the caller gets Timeout, the actor
        // takes its other mail meanwhile, and the late reply goes nowhere.
        var node = await Start("single", configure: static o => o.CallTimeout = TimeSpan.FromMilliseconds(300));
        var error = Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new LoopId(7), new Hold(1_200)))!;
        Assert.That(error.Status, Is.EqualTo(RpcStatus.Timeout));
        Assert.That(await node.Ask(new LoopId(7), new Hold(0)), Is.Zero, "the actor takes mail while the reply is kept");
        await Task.Delay(1_200); // the kept reply comes due, to nobody
        Assert.That(await node.Ask(new LoopId(7), new Hold(0)), Is.Zero);
    }

    [Test]
    public async Task AStopWithAReplyKeptForLaterNeitherSpinsNorReportsAStall()
    {
        // One shard holds a reply due in 2 s (its delayed send): the stop does not wait for it, no shard is reported
        // stalled (the fault log fails the test), and nothing spins.
        var node = await Start("single", shards: 2);
        _ = node.Ask(new LoopId(1), new Hold(2_000)).AsTask();
        await Task.Delay(100);
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        await Stop(node);
        var used = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu;
        Assert.That(used, Is.LessThan(TimeSpan.FromSeconds(1)), "the stopping shard spun instead of sleeping");
    }

    [Test]
    public async Task AStopThatExceedsItsBudgetReportsAndReturns()
    {
        Faults.Allow("BudgetExceeded"); // what the test provokes
        var store = new TestStore(new DaServer.Persistence.InMemoryActorStore());
        var node = await Start("single", shards: 1, configure: o =>
        {
            o.ShutdownTimeout = TimeSpan.FromMilliseconds(500);
            o.ActorStore = store;
        });
        Assert.That(await node.Ask(new BankId(1), new Deposit(1, false)), Is.EqualTo(1));
        store.WriteGate.Hold(); // its final save never finishes
        Assert.That(node.IsReady, Is.True);
        var phases = new List<string>();
        node.StopPhase += (phase, _) => phases.Add(phase);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await node.StopAsync();
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "the stop ends at its budget, not when the hung save ends");
        Assert.That(node.IsReady, Is.False, "a stopping node is not ready");
        Assert.That(phases, Is.EqualTo(new[] { "leave", "drain", "unload", "close" }));
        Assert.That(node.Shards[0].ActorCount, Is.EqualTo(1), "the actor whose save hangs was reported, not waited for");
        Assert.That(node.StoppedClean, Is.False, "the host exits with an error: state was lost");
        store.WriteGate.Release();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AFailedHandlerIsCountedAndItsActorResumesOrReloads(bool async)
    {
        // Async: the change is made, and fails, in the handler of the answer to an ask (the request it replies to waits
        // meanwhile): the failure reaches the caller as an error there too.
        var node = await Start("single", shards: 1);
        Assert.That(await node.Ask(new FragileId(1), new Spoil(5, false, async)), Is.EqualTo(5));
        var resumed = Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new FragileId(1), new Spoil(1, true, async)))!;
        Assert.That(resumed.Status, Is.EqualTo(RpcStatus.Error), "the failure is the caller's reply");
        Assert.That(await node.Ask(new FragileId(1), new Spoil(0, false, async)), Is.EqualTo(6), "Resume keeps the half-changed state");

        Assert.That(await node.Ask(new ReloadingFragileId(1), new Spoil(5, false, async)), Is.EqualTo(5));
        var reloaded = Assert.ThrowsAsync<RpcException>(async () => await node.Ask(new ReloadingFragileId(1), new Spoil(1, true, async)))!;
        Assert.That(reloaded.Status, Is.EqualTo(RpcStatus.Error), "the failure is the caller's reply");
        Assert.That(await node.Ask(new ReloadingFragileId(1), new Spoil(0, false, async)), Is.Zero, "Reload dropped it unsaved: loaded again");
        Assert.That(node.Failed, Is.EqualTo(2), "each failure is counted");
    }

    [Test]
    public async Task AMessageThatCannotBeReadIsRefusedWithoutFailingTheActor()
    {
        // A payload that is not the message it names (truncated here; a hostile client, another build): the caller gets
        // Malformed and nothing ran. The actor is not charged with a failure: its OnError (Reload for this type) would
        // drop what it holds unsaved, its state never touched.
        var node = await Start("single", shards: 1);
        Assert.That(await node.Ask(new ReloadingFragileId(101), new Spoil(5, false, false)), Is.EqualTo(5));
        var frame = FrameWriter.Rent();
        frame.WriteRaw(new byte[] { 1 });
        var call = PendingCall<NoReply>.Rent(null);
        var refused = call.Done;
        node.Request(frame, TypeId<Spoil>.Value, ActorIds<ReloadingFragileId>.HandlerTypeId, 101, call, null, 0);
        var error = Assert.ThrowsAsync<RpcException>(async () => await refused);
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Malformed));
        Assert.That(await node.Ask(new ReloadingFragileId(101), new Spoil(0, false, false)), Is.EqualTo(5), "the actor kept its state");
        Assert.That(node.Failed, Is.Zero, "no handler failed");
    }

    [Test]
    public async Task ReadingACallsResultBeforeItCompletedThrows()
    {
        var node = await Start("single", shards: 1);
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        node.Shards[0].Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);
        var call = node.Ask(new CounterId(1), new Add(1));
        Assert.That(() => call.AsValueTask().Result, Throws.InvalidOperationException, "a pooled call cannot block: it throws instead");
        gate.Set();
    }

    [Test]
    public async Task RequestsWhoseCallerTimedOutDoNotRun()
    {
        var node = await Start("single", shards: 1, configure: static o => o.CallTimeout = TimeSpan.FromMilliseconds(200));
        var shard = node.Shards[0];
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // the shard is busy until the gate opens
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");

        var calls = Enumerable.Range(0, 10).Select(_ => StatusOf(node.Ask(new CounterId(1), new Add(1)))).ToArray();
        Assert.That(await Task.WhenAll(calls), Is.All.EqualTo(RpcStatus.Timeout), "the callers gave up");
        await Task.Delay(100); // deadlines are best effort at millisecond granularity: stay clear of the boundary

        gate.Set();
        Assert.That(await node.Ask(new CounterId(1), new Get()), Is.Zero, "requests whose caller timed out were not run");
        Assert.That(node.Dropped(DropReason.Expired), Is.EqualTo(10));
    }

    [Test]
    public async Task AnAskThatTimedOutIsAnsweredOnTheAskersShard()
    {
        // The timeout is found by the maintenance timer, not on the asker's shard: its answer (Timeout) is the asker's
        // mail, handled on its own shard's thread.
        var node = await Start("single", shards: 2, configure: static o => o.CallTimeout = TimeSpan.FromMilliseconds(200));
        var caller = new CounterId(1);
        string callerShard = await node.Ask(caller, new WhereAmI());
        var callee = new CounterId(2);
        while (await node.Ask(callee, new WhereAmI()) == callerShard) callee = new CounterId(callee.Value + 1);
        var blocked = node.Shards.Single(s => s.Index != node.Shards.Single(x => callerShard.EndsWith("-" + x.Index)).Index);
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        blocked.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);

        Observed.ResumedOnShard = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Send(caller, new AskAndFail(callee));
        bool resumedOnShard = await Observed.ResumedOnShard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Set();
        Assert.That(resumedOnShard, Is.True);
    }

    [Test]
    public async Task ACallKeepsItsDeadlineAcrossNodes()
    {
        // The caller's remaining time travels in the request: the remote node drops it once the caller gave up.
        var caller = await Start("caller", hosts: static _ => false, configure: static o => o.CallTimeout = TimeSpan.FromMilliseconds(300));
        var target = await Start("target", shards: 1);
        await Converged();
        var shard = target.Shards[0];
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");

        var calls = Enumerable.Range(0, 10).Select(_ => StatusOf(caller.Ask(new CounterId(1), new Add(1)))).ToArray();
        Assert.That(await Task.WhenAll(calls), Is.All.EqualTo(RpcStatus.Timeout));
        await Task.Delay(100);

        gate.Set();
        Assert.That(await caller.Ask(new CounterId(1), new Get()), Is.Zero, "the remote node did not run requests past their deadline");
        Assert.That(target.Dropped(DropReason.Expired), Is.EqualTo(10));
    }

    private static async Task<RpcStatus> StatusOf<T>(Reply<T> call)
    {
        try
        {
            await call;
            return RpcStatus.Ok;
        }
        catch (RpcException e)
        {
            return e.Status;
        }
    }
}
