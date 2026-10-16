namespace DaServer.Tests;

/// <summary>Bounded queues: a flood is refused with <see cref="RpcStatus.Overloaded"/> instead of growing memory.</summary>
public class BackpressureTests : ClusterTest
{
    [Test]
    public async Task AFullActorMailboxRefusesNewCalls()
    {
        var node = await Start("single", shards: 1, configure: static o => o.ActorMailboxCapacity = 4);
        // Each request awaits 10 ms inside its turn, so the requests pile up behind the first one.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => StatusOf(node.Ask(new CounterId(1), new SlowAdd(1)))));

        int ok = statuses.Count(s => s == RpcStatus.Ok), overloaded = statuses.Count(s => s == RpcStatus.Overloaded);
        Assert.That(ok + overloaded, Is.EqualTo(50), "every call either ran or was refused");
        // All 50 may arrive before the first turn starts (loaded actor): then exactly the capacity is admitted.
        Assert.That(ok, Is.InRange(4, 49), "the mailbox capacity was used");
        Assert.That(await node.Ask(new CounterId(1), new Get()), Is.EqualTo(ok), "refused calls did not run");
        Assert.That(node.Rejected, Is.EqualTo(overloaded));
    }

    [Test]
    public async Task AFullShardRefusesNewCallsAndRecovers()
    {
        var node = await Start("single", shards: 1, configure: static o => o.DispatchQueueCapacity = 16);
        var shard = node.Shards[0];
        using var gate = new ManualResetEventSlim();
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
        Assert.That(node.Rejected, Is.EqualTo(84));
    }

    [Test]
    public async Task AShardThreadBlockedLongerThanStallAfterIsReported()
    {
        var node = await Start("single", shards: 1, configure: static o => o.StallAfter = TimeSpan.FromMilliseconds(200));
        using var gate = new ManualResetEventSlim();
        node.Shards[0].Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // what DAS009 reports in user code
        await Eventually(() => node.StalledShards == 1, "the watchdog reports the blocked shard");
        gate.Set();
        await Eventually(() => node.StalledShards == 0, "and clears it once the shard is back in its loop");
    }

    [Test]
    public async Task ReadingACallsResultBeforeItCompletedThrows()
    {
        var node = await Start("single", shards: 1);
        using var gate = new ManualResetEventSlim();
        node.Shards[0].Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);
        var call = node.Ask(new CounterId(1), new Add(1));
        Assert.That(() => call.Result, Throws.InvalidOperationException, "a pooled call cannot block: it throws instead");
        gate.Set();
    }

    [Test]
    public async Task RequestsWhoseCallerTimedOutDoNotRun()
    {
        var node = await Start("single", shards: 1, configure: static o => o.CallTimeout = TimeSpan.FromMilliseconds(200));
        var shard = node.Shards[0];
        using var gate = new ManualResetEventSlim();
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // the shard is busy until the gate opens
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");

        var calls = Enumerable.Range(0, 10).Select(_ => StatusOf(node.Ask(new CounterId(1), new Add(1)))).ToArray();
        Assert.That(await Task.WhenAll(calls), Is.All.EqualTo(RpcStatus.Timeout), "the callers gave up");
        await Task.Delay(100); // deadlines are best effort at millisecond granularity: stay clear of the boundary

        gate.Set();
        Assert.That(await node.Ask(new CounterId(1), new Get()), Is.Zero, "requests whose caller timed out were not run");
        Assert.That(node.Expired, Is.EqualTo(10));
    }

    [Test]
    public async Task AnActorWhoseCallTimedOutResumesOnItsShard()
    {
        // The timeout is found by the maintenance timer, not on the caller's shard: the caller's code must not run there.
        var node = await Start("single", shards: 2, configure: static o => o.CallTimeout = TimeSpan.FromMilliseconds(200));
        var caller = new CounterId(1);
        string callerShard = await node.Ask(caller, new WhereAmI());
        var callee = new CounterId(2);
        while (await node.Ask(callee, new WhereAmI()) == callerShard) callee = new CounterId(callee.Value + 1);
        var blocked = node.Shards.Single(s => s.Index != node.Shards.Single(x => callerShard.EndsWith("-" + x.Index)).Index);
        using var gate = new ManualResetEventSlim();
        blocked.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);

        Reports.ResumedOnShard = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Send(caller, new AskAndFail(callee));
        bool resumedOnShard = await Reports.ResumedOnShard.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        using var gate = new ManualResetEventSlim();
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate);
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");

        var calls = Enumerable.Range(0, 10).Select(_ => StatusOf(caller.Ask(new CounterId(1), new Add(1)))).ToArray();
        Assert.That(await Task.WhenAll(calls), Is.All.EqualTo(RpcStatus.Timeout));
        await Task.Delay(100);

        gate.Set();
        Assert.That(await caller.Ask(new CounterId(1), new Get()), Is.Zero, "the remote node did not run requests past their deadline");
        Assert.That(target.Expired, Is.EqualTo(10));
    }

    private static async Task<RpcStatus> StatusOf<T>(ValueTask<T> call)
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
