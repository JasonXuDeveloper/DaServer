namespace DaServer.Tests;

/// <summary>Zero-GC contract of the RPC hot path (correctness guard; numbers come from DaServer.Benchmark).</summary>
public class AllocationTests : ClusterTest
{
    [Test, Retry(3)] // process-wide counters also see other work under CPU contention: a real regression fails every try
    public async Task ShardToShardCallsDoNotAllocate()
    {
        // No gossip during the measurement: background membership traffic is not part of the call path.
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));

        // Two keys on different shards: every call crosses threads through the mailboxes.
        long other = 2;
        while (await node.Ask(new CounterId(other), new WhereAmI()) == await node.Ask(new CounterId(1), new WhereAmI())) other++;

        // Warm up past tiered-JIT promotion, then measure. The counter is process-wide, so background runtime work
        // (timers, thread-pool bookkeeping: 0.15 B per call locally, up to ~2.5 B on a 2-core CI runner) is included;
        // anything per call would be >= 24 B (the smallest object), so 8 B still catches it.
        const int calls = 20_000;
        await node.Ask(new CounterId(1), new AddMany(new CounterId(other), 3 * calls));
        long allocated = await node.Ask(new CounterId(1), new AddMany(new CounterId(other), calls));
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} calls");
    }

    [Test, Retry(3)]
    public async Task RepliesOfSuspendedHandlersDoNotAllocate()
    {
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        const int calls = 10_000;
        await node.Ask(new CounterId(1), new AddViaMany(new CounterId(2), new CounterId(3), 3 * calls));
        long allocated = await node.Ask(new CounterId(1), new AddViaMany(new CounterId(2), new CounterId(3), calls));
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} calls");
    }

    [Test, Retry(3)]
    public async Task RepliesSentLaterDoNotAllocate()
    {
        // Every call is replied with ReplyWhen once a third actor answered: the deferred reply's watcher is pooled.
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        const int calls = 10_000;
        await node.Ask(new CounterId(1), new ConfirmViaMany(new CounterId(2), new CounterId(3), 3 * calls));
        long allocated = await node.Ask(new CounterId(1), new ConfirmViaMany(new CounterId(2), new CounterId(3), calls));
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} calls");
    }

    [Test, Retry(3)]
    public async Task DelayedSendsDoNotAllocate()
    {
        // Scheduled, held by the shard's timer wheel and sent when due: frames and wheel entries are reused.
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        const int sends = 2_000; // below the target's mailbox bound
        var (scheduler, target) = (new CounterId(1), new CounterId(2));
        for (int round = 1; round <= 3; round++) // warm-up: wheel entries, frames
        {
            await node.Ask(scheduler, new BumpManyLater(target, sends));
            await Eventually(async () => await node.Ask(target, new Get()) == round * sends, "the delayed sends did not arrive");
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        await node.Ask(scheduler, new BumpManyLater(target, sends));
        await Eventually(async () => await node.Ask(target, new Get()) == 4 * sends, "the delayed sends did not arrive");
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.That(allocated / (double)sends, Is.LessThan(8), $"{allocated} B for {sends} delayed sends");
    }

    [Test, Retry(3)]
    public async Task BurstsOfConcurrentCallsDoNotAllocate()
    {
        // 2,000 calls in flight at once: above the old fixed pool sizes (1,024), below the callee's mailbox bound.
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        long other = 2;
        while (await node.Ask(new CounterId(other), new WhereAmI()) == await node.Ask(new CounterId(1), new WhereAmI())) other++;

        const int width = 2_000, rounds = 10;
        await node.Ask(new CounterId(1), new AskInBursts(new CounterId(other), width, 3));
        long allocated = await node.Ask(new CounterId(1), new AskInBursts(new CounterId(other), width, rounds));
        Assert.That(allocated / (double)(width * rounds), Is.LessThan(8), $"{allocated} B for {width * rounds} calls");
    }

    [Test, Retry(3)]
    public async Task BurstsOfSuspendedHandlersDoNotAllocate()
    {
        // 2,000 actors suspended in async Reply<T> handlers at once: their state machines live in pooled boxes.
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        const int width = 2_000, rounds = 10;
        await node.Ask(new CounterId(1), new SuspendInBursts(1_000, new CounterId(2), width, 20));
        long allocated = await node.Ask(new CounterId(1), new SuspendInBursts(1_000, new CounterId(2), width, rounds));
        Assert.That(allocated / (double)(width * rounds), Is.LessThan(8), $"{allocated} B for {width * rounds} suspended handlers");
    }

    [Test, Retry(3)]
    public async Task CheckpointsOfUnchangedActorsDoNotAllocate()
    {
        // 99 actors checkpointing every 100 ms: each check serializes the record into a pooled frame and hashes it.
        var node = await Start("alloc", configure: o =>
        {
            o.GossipInterval = TimeSpan.FromHours(1);
            o.ActorStore = new DaServer.Persistence.InMemoryActorStore();
        });
        for (int i = 1; i < 100; i++) await node.Ask(new BankId(i), new Deposit(1, false));
        Thread.Sleep(1000); // first checkpoints written, pools warm
        // ~1,000 checkpoints a second: anything allocated per checkpoint would be >= 24 KB/s; the test process alone
        // makes about 1.7 KB/s (see IdleNodesDoNotAllocate), so the quietest of a few windows is compared.
        long quietest = long.MaxValue;
        for (int second = 0; second < 4; second++)
        {
            var meter = new AllocationMeter();
            Thread.Sleep(1000);
            quietest = Math.Min(quietest, meter.Garbage);
        }

        Assert.That(quietest, Is.LessThan(8 * 1024), $"{quietest} B/s while checkpointing");
    }

    [Test, Retry(3)]
    public async Task SavingChangedRecordsDoesNotAllocate()
    {
        // 99 actors whose record changes every tick, checkpointed every 100 ms: about 1,000 writes a second, each
        // serialized into a pooled frame, batched, stored (in place, like a database row) and applied.
        var store = new DaServer.Persistence.InMemoryActorStore();
        var node = await Start("alloc", configure: o =>
        {
            o.GossipInterval = TimeSpan.FromHours(1);
            o.ActorStore = store;
        });
        for (int i = 1; i < 100; i++) await node.Ask(new BankId(i), new Earn());
        Thread.Sleep(1000);
        long before = await Versions(store);
        long quietest = long.MaxValue;
        for (int second = 0; second < 4; second++)
        {
            var meter = new AllocationMeter();
            Thread.Sleep(1000);
            quietest = Math.Min(quietest, meter.Garbage);
        }

        long written = await Versions(store) - before;
        Assert.That(written, Is.GreaterThan(2_000), "the records changed and were written");
        Assert.That(quietest, Is.LessThan(8 * 1024), $"{quietest} B/s while writing {written / 4} changed records a second");
    }

    /// <summary>The sum of the banks' record versions: one per write (and one per claim).</summary>
    private static async Task<long> Versions(DaServer.Persistence.IActorStore store)
    {
        long sum = 0;
        for (int i = 0; i < 99; i++)
            sum += (await Stores.Peek(store, new DaServer.Persistence.StoredKey(ActorIds<BankId>.HandlerTypeId, i + 1))).Version;
        return sum;
    }

    [Test, Retry(3)]
    public async Task KeyedEventsDoNotAllocate()
    {
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        long other = 2;
        while (await node.Ask(new CounterId(other), new WhereAmI()) == await node.Ask(new CounterId(1), new WhereAmI())) other++;

        const int events = 20_000;
        await node.Ask(new CounterId(1), new SendMany(new CounterId(other), 3 * events));
        long allocated = await node.Ask(new CounterId(1), new SendMany(new CounterId(other), events));
        Assert.That(allocated / (double)events, Is.LessThan(8), $"{allocated} B for {events} events"); // see above
    }

    [Test, Retry(3)]
    public async Task GatewayFanOutDoesNotAllocate([Values(0, 2)] int flushMs)
    {
        // Written at once, or on a 2 ms beat (ClientConnectionOptions.FlushInterval).
        var gateway = await Start("gw", gateway: true, configure: o => o.GossipInterval = TimeSpan.FromHours(1),
            clients: o => o.FlushInterval = TimeSpan.FromMilliseconds(flushMs));
        var receiver = new GoldCounter();
        const int members = 16;
        for (int i = 0; i < members; i++)
        {
            var client = await Connect(gateway);
            client.Register(receiver);
            await client.Ask(new Enter(new RoomId(7)));
        }

        var group = ClientGroup.Of(new RoomId(7)); // the room's Group

        // Server side and client side both count: serialize once, one copy per session, client dispatch.
        const int broadcasts = 5_000;
        Broadcast(3 * broadcasts);
        var meter = new AllocationMeter();
        Broadcast(broadcasts);
        long allocated = meter.Garbage;
        Assert.That(allocated / (double)(broadcasts * members), Is.LessThan(1), $"{allocated} B for {broadcasts} broadcasts to {members}");
        Assert.That(meter.PoolGrowth, Is.LessThan(1 << 20), "pooled buffers reach their peak during warm-up");

        // Synchronous waits: an await here would allocate in the test harness, not in the path under test.
        void Broadcast(int count)
        {
            for (int i = 0; i < count; i++)
            {
                gateway.Send(group, new GoldChanged(i));
                if (i % 64 == 63) // stay within the clients' send backlog
                    WaitFor(ref receiver.Received, (long)(i + 1) * members);
            }

            WaitFor(ref receiver.Received, (long)count * members);
            Interlocked.Exchange(ref receiver.Received, 0);
        }
    }

    [Test, Retry(3)]
    public async Task GroupChurnDoesNotAllocate()
    {
        // Sessions joining and leaving groups that come and go (area of interest cells as players move).
        var gateway = await Start("gw", gateway: true, configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        for (int i = 0; i < 16; i++) await Connect(gateway);
        await Eventually(() => gateway.SessionCount() == 16, "sessions");
        var sessions = gateway.Feature<ClientConnections>()!.SessionIds().Select(id => new ClientSession(gateway.Id, id)).ToArray();

        const int rounds = 5_000;
        Churn(3 * rounds);
        var meter = new AllocationMeter();
        Churn(rounds);
        long allocated = meter.Garbage;
        Assert.That(allocated / (double)(rounds * sessions.Length * 2), Is.LessThan(1), $"{allocated} B for {rounds * sessions.Length * 2} joins and leaves");

        void Churn(int count)
        {
            for (int round = 0; round < count; round++)
            {
                var group = ClientGroup.Of<GroupChurn>(round % 64); // groups emptied and created again
                foreach (var session in sessions) gateway.Send(session, new JoinGroup(group));
                foreach (var session in sessions) gateway.Send(session, new LeaveGroup(group));
            }
        }
    }

    private struct GroupChurn;

    [Test, Retry(3)]
    public async Task ClientCallsThroughAGatewayToAnotherNodeDoNotAllocate()
    {
        var gateway = await Start("gw", gateway: true, hosts: static _ => false, configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        var game = await Start("game", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        await Converged();
        var client = await Connect(gateway);
        await client.Ask(new Login(new PlayerId(42))); // binds the session to player 42 (on the game node)

        // One-way client input (the dominant client traffic) crossing gateway -> game.
        const int calls = 5_000;
        await Calls(3 * calls);
        var meter = new AllocationMeter();
        await Calls(calls);
        long allocated = meter.Garbage;
        // Anything per call would be >= 24 B; below that is the harness (the test's awaits).
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} client calls");
        // A burst above the warm-up peak may rent a few more pooled buffers (send batches, resend window chunks).
        Assert.That(meter.PoolGrowth, Is.LessThan(1 << 20), "pooled buffers stay near their warm-up peak");

        // Batches of 500 with one await each: the reply comes after the batch's tips (one ordered stream per direction),
        // so send backlogs stay as small as during warm-up (a larger burst would grow a pooled buffer to a new size
        // class once), and the few test-side awaits allocate little in the harness.
        async ValueTask Calls(int count)
        {
            for (int i = 1; i <= count; i++)
            {
                client.Send(new Tip(1));
                if (i % 500 == 0) await client.Ask(new AddGold(0));
            }
        }
    }

    [Test, Retry(3)]
    public async Task GossipRoundsDoNotAllocate()
    {
        // Three members gossiping every 10 ms (about 300 rounds a second between them), idle otherwise.
        for (int i = 0; i < 3; i++)
            await Start("gossip-" + i, configure: static o => o.GossipInterval = TimeSpan.FromMilliseconds(10));
        await Converged();
        Thread.Sleep(2000); // cards exchanged, links, pools and tiered JIT warm

        var meter = new AllocationMeter();
        Thread.Sleep(2000);
        long allocated = meter.Garbage;
        // Exchanging full tables cost an idle six-node topology about 10 KB/s per node at one round a second; pulses
        // are read in place. What remains is the process background (timers: about 2 KB/s with gossip off).
        Assert.That(allocated / 600.0, Is.LessThan(32), $"{allocated} B for about 600 rounds");
    }

    [Test, Retry(3)]
    public async Task CallsToAnotherNodeDoNotAllocate()
    {
        // The measuring call lasts as long as its 60,000 round trips.
        var caller = await Start("caller", configure: static o => (o.GossipInterval, o.CallTimeout) = (TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)));
        var callee = await Start("callee", configure: static o => o.GossipInterval = TimeSpan.FromHours(1));
        await Converged();
        long from = KeyOwnedBy<CounterId>(caller), to = KeyOwnedBy<CounterId>(callee);

        const int calls = 20_000;
        await caller.Ask(new CounterId(from), new AddMany(new CounterId(to), 3 * calls));
        long allocated = await caller.Ask(new CounterId(from), new AddMany(new CounterId(to), calls));
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} calls"); // see above
    }

    [TestCase(false, TestName = "ServiceTrafficDoesNotAllocate(local)"), Retry(3)]
    [TestCase(true, TestName = "ServiceTrafficDoesNotAllocate(remote)")]
    public async Task ServiceTrafficDoesNotAllocate(bool remote)
    {
        // Remote: the services live only on the second node, so every message crosses the link.
        var caller = await Start("caller", hosts: remote ? static t => t == typeof(Counter) : null,
            configure: static o => (o.GossipInterval, o.CallTimeout) = (TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)));
        if (remote) await Start("services", hosts: static t => t != typeof(Counter), configure: static o => o.GossipInterval = TimeSpan.FromHours(1));
        await Converged();

        const int rounds = 10_000;
        await caller.Ask(new CounterId(1), new ServiceTraffic(3 * rounds));
        long allocated = await caller.Ask(new CounterId(1), new ServiceTraffic(rounds));
        // Per round: one message, one event to two services, one request and its reply.
        Assert.That(allocated / (double)rounds, Is.LessThan(8), $"{allocated} B for {rounds} rounds");
    }

    [Test, Retry(3)]
    public async Task ActorChurnDoesNotAllocate()
    {
        // Actors loaded by a message and unloaded when idle, again and again: the actor object, its mailbox and its
        // entity's storage are reused.
        var node = await Start("churn", configure: static o => o.GossipInterval = TimeSpan.FromHours(1));
        const int actors = 1_000;
        for (int round = 0; round < 3; round++) Churn();
        var meter = new AllocationMeter();
        Churn();
        long allocated = meter.Garbage;
        Assert.That(allocated / (double)actors, Is.LessThan(8), $"{allocated} B for {actors} loads and unloads");

        // Synchronous waits: an await here would allocate in the test harness, not in the path under test.
        void Churn()
        {
            long knocks = Interlocked.Read(ref Observed.Knocks) + actors;
            for (int i = 0; i < actors; i++) node.Send(new GuestId(i + 1), new Knock());
            WaitFor(ref Observed.Knocks, knocks);
            var spin = new SpinWait();
            while (node.ActorCount > 0) spin.SpinOnce(); // unloaded by the idle scan (a few times a second)
        }
    }

    [Test, Retry(3)]
    public async Task RefusalsDoNotAllocate()
    {
        // Overload is when refusals happen most: refusing must not add GC work. Refusals are status-only frames.
        var node = await Start("refuse", shards: 1, configure: static o =>
        {
            o.GossipInterval = TimeSpan.FromHours(1);
            o.DispatchQueueCapacity = 16;
        });
        var shard = node.Shards[0];
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // the shard is busy until the gate opens
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");
        var sink = new StatusSink();
        for (int i = 0; i < 16; i++) // the dispatch queue is full (these run, and succeed, once the gate opens)
        {
            var add = FrameWriter.Rent();
            add.Write(new Add(1));
            node.Request(add, TypeId<Add>.Value, ActorIds<CounterId>.HandlerTypeId, 1, null, sink, 1);
        }

        const int refusals = 10_000;
        Refuse(3 * refusals);
        var meter = new AllocationMeter();
        Refuse(refusals);
        long allocated = meter.Garbage;
        gate.Set();

        Assert.That(sink.Overloaded, Is.EqualTo(4 * refusals));
        Assert.That(sink.NotHosted, Is.EqualTo(4 * refusals));
        Assert.That(sink.PayloadBytes, Is.Zero, "status-only frames");
        Assert.That(allocated / (double)(2 * refusals), Is.LessThan(8), $"{allocated} B for {2 * refusals} refusals");

        void Refuse(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var frame = FrameWriter.Rent(FrameHeader.Size);
                frame.SetCallId(1);
                frame.SetHeader(FrameKind.Message, ActorIds<CounterId>.HandlerTypeId, TypeId<Add>.Value, 1);
                Node.Admit(shard, frame, sink); // full shard: Overloaded
                node.Request(FrameWriter.Rent(FrameHeader.Size), 0xDEAD, 0, 0, null, sink, 1); // nobody: NotHosted
            }
        }
    }

    [Test, Retry(3)]
    public async Task TimeoutsOfRelayedCallsDoNotAllocate()
    {
        // Relayed requests (as a gateway forwards client calls) whose deadline passes while the target shard is busy:
        // the sweep answers each with a status-only Timeout, and the shard later drops them unrun.
        var node = await Start("timeout", shards: 1, configure: static o => o.GossipInterval = TimeSpan.FromHours(1));
        var shard = node.Shards[0];
        var sink = new StatusSink();
        const int calls = 2_000;
        for (int round = 0; round < 3; round++) Relay(calls);

        // Each window waits for the 100 ms sweep, so it also counts whatever else the process allocates meanwhile (a
        // thread pool thread injected, an earlier test's nodes finishing their shutdown). A steady per-call cost shows
        // in every window: the quietest of three is the path's own.
        long allocated = long.MaxValue;
        for (int window = 0; window < 3; window++)
        {
            var meter = new AllocationMeter();
            Relay(calls);
            allocated = Math.Min(allocated, meter.Garbage);
        }

        Assert.That(sink.Timeout, Is.EqualTo(6 * calls));
        Assert.That(node.Expired, Is.EqualTo(6 * calls), "the shard dropped the expired requests unrun");
        Assert.That(sink.PayloadBytes, Is.Zero, "status-only frames");
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} timeouts");

        // Synchronous waits: an await here would allocate in the test harness, not in the path under test.
        void Relay(int count)
        {
            var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
            shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // the shard is busy until the gate opens
            long timedOut = Interlocked.Read(ref sink.Timeout) + count, expired = node.Expired + count;
            for (int i = 0; i < count; i++)
            {
                var frame = FrameWriter.Rent();
                frame.Write(new Add(1));
                frame.Deadline = Environment.TickCount64 + 1;
                node.Request(frame, TypeId<Add>.Value, ActorIds<CounterId>.HandlerTypeId, 1, null, sink, (uint)i + 1);
            }

            WaitFor(ref sink.Timeout, timedOut);
            gate.Set();
            var spin = new SpinWait();
            while (node.Expired < expired) spin.SpinOnce(); // frames back in their pool
        }
    }

    /// <summary>Counts error replies by status (any thread); returns every response frame to its pool.</summary>
    private sealed class StatusSink : IReplySink
    {
        public long Overloaded, NotHosted, Timeout, PayloadBytes;

        public void SendReply(uint requestId, RpcStatus status, FrameWriter response)
        {
            if (status != RpcStatus.Ok) Interlocked.Add(ref PayloadBytes, response.Payload.Length);
            response.Dispose();
            if (status == RpcStatus.Overloaded) Interlocked.Increment(ref Overloaded);
            else if (status == RpcStatus.NotHosted) Interlocked.Increment(ref NotHosted);
            else if (status == RpcStatus.Timeout) Interlocked.Increment(ref Timeout);
        }
    }

    [Test, Retry(3)]
    public async Task IdleNodesDoNotAllocate()
    {
        // 500 ticks a second on each of 4 shards, maintenance and gossip timers running: anything allocated per tick or
        // per wake would be >= 48 KB/s. What remains is the test process (about 1.7 KB/s with no node at all).
        // The quietest of several one-second windows: one-time work (tiered JIT, the test host warming up) lands in some
        // windows, anything per tick in all of them.
        await Start("idle", shards: 4, configure: static o => o.TickRate = 500);
        long quietest = long.MaxValue;
        for (int second = 0; second < 8; second++)
        {
            var meter = new AllocationMeter();
            Thread.Sleep(1000);
            quietest = Math.Min(quietest, meter.Garbage);
        }

        Assert.That(quietest, Is.LessThan(8 * 1024), $"{quietest} B/s idle");
    }

    /// <summary>
    /// Garbage allocated (process-wide) since construction: every allocation minus <see cref="BufferPool"/> growth,
    /// which is pinned memory the pool keeps for reuse once returned, not GC pressure. Growth is reported apart.
    /// </summary>
    private readonly struct AllocationMeter()
    {
        private readonly long _total = GC.GetTotalAllocatedBytes(precise: true);
        private readonly long _pool = BufferPool.AllocatedBytes;

        public long PoolGrowth => BufferPool.AllocatedBytes - _pool;
        public long Garbage => GC.GetTotalAllocatedBytes(precise: true) - _total - PoolGrowth;
    }

    private static void WaitFor(ref long counter, long target)
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref counter) < target) spin.SpinOnce();
    }

    internal sealed class GoldCounter
    {
        public long Received;

        [Handle] public void On(GoldChanged _) => Interlocked.Increment(ref Received);
    }
}
