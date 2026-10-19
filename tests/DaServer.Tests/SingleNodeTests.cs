namespace DaServer.Tests;

/// <summary>RPC semantics on one node (local delivery through shard mailboxes).</summary>
public class SingleNodeTests : ClusterTest
{
    private Node _node = null!;

    [SetUp]
    public async Task StartNode() => _node = await Start("single", shards: 4);

    [Test]
    public async Task ActorsKeepStatePerKey()
    {
        Assert.That(await _node.Ask(new CounterId(1), new Add(5)), Is.EqualTo(5));
        Assert.That(await _node.Ask(new CounterId(1), new Add(2)), Is.EqualTo(7));
        Assert.That(await _node.Ask(new CounterId(2), new Get()), Is.Zero);
        Assert.That(await _node.Ask(new CounterId(1), new EchoPoint(new Point(1, 2))), Is.EqualTo(new Point(2, 2)));
    }

    [Test]
    public async Task IdleActorsUnloadAndLoadAgainOnTheNextMessage()
    {
        const long key = 501;
        Assert.That(await _node.Ask(new VisitorId(key), new Visit()), Is.EqualTo(1));

        // Kept loaded while it receives mail more often than its UnloadAfterIdle (300 ms).
        for (int i = 2; i <= 5; i++)
        {
            await Task.Delay(100);
            Assert.That(await _node.Ask(new VisitorId(key), new Visit()), Is.EqualTo(i));
        }

        Assert.That(Observed.Events, Has.No.Member($"unload:{key}"));
        await Eventually(() => Observed.Events.Contains($"unload:{key}"), "the idle actor was not unloaded");
        await Eventually(() => _node.ActorCount == 0, "the unloaded actor is still in the directory");

        Assert.That(await _node.Ask(new VisitorId(key), new Visit()), Is.EqualTo(1), "loaded again with fresh state");
        Assert.That(Observed.Events.Count(e => e == $"load:{key}"), Is.EqualTo(2));
    }

    [Test]
    public async Task ActorsOfANodeDeclaredDeadAreRemovedWithoutUnload()
    {
        const long key = 502;
        Assert.That(await _node.Ask(new KeeperId(key), new Visit()), Is.EqualTo(1));
        // The count is published at the end of the shard's pass, which may follow the reply: wait until it shows the
        // actor, or "back to 0" below could be the count from before it loaded.
        await Eventually(() => _node.ActorCount == 1, "the loaded actor is not counted");

        // The cluster gave the key to another node: a save from OnUnload could overwrite the new owner's state.
        foreach (var shard in _node.Shards) shard.RequestUnloadAll();
        await Eventually(() => _node.ActorCount == 0, "the fenced actor is still in the directory");
        Assert.That(Observed.Events, Has.No.Member($"keeper-unload:{key}"));

        Assert.That(await _node.Ask(new KeeperId(key), new Visit()), Is.EqualTo(1), "loaded again once the node is Up");
    }

    [Test]
    public async Task CallsFromOneSenderArriveInOrder()
    {
        _node.Send(new CounterId(3), new Bump());    // one-way messages are queued before the request below
        _node.Send(new CounterId(3), new Bump());
        Assert.That(await _node.Ask(new CounterId(3), new Get()), Is.EqualTo(2));
        await _node.Ask(new CounterId(3), new Reset());
        Assert.That(await _node.Ask(new CounterId(3), new Get()), Is.Zero);
    }

    [Test]
    public async Task ConcurrentCallsToOneActorAreSerialized()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => _node.Ask(new CounterId(4), new Add(1)).AsTask()));
        Assert.That(results.Order(), Is.EqualTo(Enumerable.Range(1, 1000)), "no lost or duplicated updates");

        string shard = await _node.Ask(new CounterId(4), new WhereAmI());
        Assert.That(shard, Does.Contain("shard"));
        Assert.That(await _node.Ask(new CounterId(4), new WhereAmI()), Is.EqualTo(shard), "an actor stays on one shard");
    }

    [Test]
    public async Task AwaitsResumeOnTheActorsShard()
    {
        Assert.That(await _node.Ask(new CounterId(10), new AddVia(new CounterId(11), 3)), Is.EqualTo(3));
        Assert.That(await _node.Ask(new CounterId(11), new Get()), Is.EqualTo(3));
        Assert.That(await _node.Ask(new CounterId(12), new SlowAdd(4)), Is.EqualTo(4));
    }

    [Test]
    public async Task ServicesRunOnTheLocalNode()
    {
        Assert.That(await _node.Ask(new EchoText("hi")), Is.EqualTo("hi"));
        Assert.That(await _node.Ask(new WhichNode()), Is.EqualTo(_node.Id));
    }

    [Test]
    public async Task ExceptionsBecomeRpcErrorsAndTheActorSurvives()
    {
        var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Ask(new CounterId(5), new Fail("boom")));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Error));
        Assert.That(error.Message, Does.Contain("boom"));
        Assert.That(await _node.Ask(new CounterId(5), new Add(1)), Is.EqualTo(1));
    }

    [Test]
    public async Task SystemsProcessActorStateEveryTick()
    {
        await _node.Ask(new CounterId(6), new Grow()); // the actor writes input; GrowthSystem does the per-tick work
        int first = await _node.Ask(new CounterId(6), new Get());
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // 50 ticks/s
        Assert.That(await _node.Ask(new CounterId(6), new Get()), Is.GreaterThan(first));
        Assert.That(await _node.Ask(new CounterId(7), new Get()), Is.Zero, "only growing counters grow");
    }

    [Test]
    public async Task ASystemThatThrowsOnEveryPassLeavesTheShardServing()
    {
        // It throws before the runtime's mail systems of the same group on every wake: they run anyway.
        Faults.Allow("SystemFailed"); // what the test provokes
        await _node.Ask(new CounterId(8), new Grow());
        long failed = _node.Failed;
        FaultySystem.Throwing.Value = 1;
        try
        {
            await Eventually(() => Faults.Count("SystemFailed") > 0, "the system threw");
            int after = await _node.Ask(new CounterId(8), new Get());
            await Eventually(async () => await _node.Ask(new CounterId(8), new Get()) > after, "the shard still ticks its systems");
            Assert.That(await _node.Ask(new CounterId(8), new Add(1)), Is.GreaterThan(after), "and serves its actors");
            Assert.That(Faults.Count("SystemFailed"), Is.LessThanOrEqualTo(4), "logged at most once a second per shard");
            Assert.That(_node.Failed, Is.GreaterThan(failed + Faults.Count("SystemFailed")), "every failing pass is counted, logged or not");
        }
        finally
        {
            FaultySystem.Throwing.Value = 0;
        }
    }

    [Test]
    public async Task AShardWhoseWorldCannotBeBuiltReadsAsStalled()
    {
        Faults.Allow("ShardCrashed"); // what the test provokes, and what the watchdog then reports
        Faults.Allow("ShardStalled");
        FaultySystem.CreateThrows.Value = 1; // until the shard thread built its world: the start does not wait for it
        try
        {
            var broken = await Start("broken", shards: 1);
            await Eventually(() => broken.StalledShards == 1, "the crashed shard reads as stalled, so /alive fails");
        }
        finally
        {
            FaultySystem.CreateThrows.Value = 0;
        }
    }

    [Test]
    public async Task AnActorWhoseConstructionThrowsFailsItsMessageAndLoadsLater()
    {
        Faults.Allow("ActorFailed"); // what the test provokes
        Brittle.ConstructorThrows.Value = 1;
        try
        {
            var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Ask(new BrittleId(1), new Poke()));
            Assert.That(error!.Status, Is.EqualTo(RpcStatus.Error), "answered, not left to time out");
        }
        finally
        {
            Brittle.ConstructorThrows.Value = 0;
        }

        Brittleness.Breaks.Value = 1;
        try
        {
            var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Ask(new BrittleId(2), new Poke()));
            Assert.That(error!.Status, Is.EqualTo(RpcStatus.Error), "a state initializer that throws fails the load");
        }
        finally
        {
            Brittleness.Breaks.Value = 0;
        }

        Assert.That(await _node.Ask(new BrittleId(1), new Poke()), Is.EqualTo(1), "the key is not stuck");
        Assert.That(await _node.Ask(new BrittleId(2), new Poke()), Is.EqualTo(1), "nor is this one");
    }

    [Test]
    public async Task DispatchFiltersRefuseBeforeTheHandlerRuns()
    {
        var filter = MessageFilters.Register<Guarded>(static (object _, in Guarded m, in CallContext _) => m.N < 0 ? RpcStatus.Forbidden : RpcStatus.Ok);
        Assert.That(await _node.Ask(new Guarded(7)), Is.EqualTo(7));
        var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Ask(new Guarded(-1)));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Forbidden));
        filter.Dispose();
        Assert.That(await _node.Ask(new Guarded(-1)), Is.EqualTo(-1), "a disposed registration filters nothing");
    }

    [Test]
    public async Task AFilterAttributeRegistersItself()
    {
        Assert.That(await _node.Ask(new Screened(3)), Is.EqualTo(3));
        var error = Assert.ThrowsAsync<RpcException>(async () => await _node.Ask(new Screened(-1)));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Forbidden), "ScreenRules ran before the handler");
    }

    [Test]
    public async Task ARepliedLaterRequestEndsItsTurnAtOnce()
    {
        var node = _node;
        var counter = new CounterId(31);
        Assert.That(await node.Ask(counter, new Add(5)), Is.EqualTo(5));

        var later = node.Ask(counter, new AnswerLater(500, Fail: false)).AsTask();
        Assert.That(await node.Ask(counter, new Add(1)), Is.EqualTo(6), "the actor took its next message while the reply waits");
        Assert.That(later.IsCompleted, Is.False);
        Assert.That(await later, Is.EqualTo(5), "the value given when the turn ended");

        var failed = Assert.ThrowsAsync<RpcException>(async () => await node.Ask(counter, new AnswerLater(0, Fail: true)));
        Assert.That(failed!.Status, Is.EqualTo(RpcStatus.Error), "a failed task is replied as the failure");
    }

    [Test]
    public async Task DelayedSendsArriveLaterAndCanBeCancelled()
    {
        var (scheduler, target) = (new CounterId(41), new CounterId(42));
        long timer = await _node.Ask(scheduler, new BumpLater(target, 300, 5));
        Assert.That(await _node.Ask(target, new Get()), Is.Zero, "not before its delay");
        await Eventually(async () => await _node.Ask(target, new Get()) == 5, "the delayed send did not arrive");
        Assert.That(await _node.Ask(scheduler, new CancelBump(timer)), Is.False, "already sent");

        long cancelled = await _node.Ask(scheduler, new BumpLater(target, 300, 7));
        Assert.That(await _node.Ask(scheduler, new CancelBump(cancelled)), Is.True);
        await Task.Delay(500);
        Assert.That(await _node.Ask(target, new Get()), Is.EqualTo(5), "a cancelled send is never sent");

        Assert.Throws<InvalidOperationException>(() => _node.SendAfter(TimeSpan.Zero, target, new CounterBumped(1)),
            "timers are a shard's data: off a shard thread there is none");
    }
}
