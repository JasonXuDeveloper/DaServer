using System;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Zero-GC contract of the RPC hot path (correctness guard; numbers come from DaServer.Benchmark).</summary>
public class AllocationTests : ClusterTest
{
    [Test]
    public async Task ShardToShardCallsDoNotAllocate()
    {
        // No gossip during the measurement: background membership traffic is not part of the call path.
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));

        // Two keys on different shards: every call crosses threads through the mailboxes.
        long other = 2;
        while (await node.Counter(other).WhereAmI() == await node.Counter(1).WhereAmI()) other++;

        // Warm up past tiered-JIT promotion, then measure. The counter is process-wide, so background runtime work
        // (timers, thread-pool bookkeeping: 0.15 B per call locally, up to ~2.5 B on a 2-core CI runner) is included;
        // anything per call would be >= 24 B (the smallest object), so 8 B still catches it.
        const int calls = 20_000;
        await node.Counter(1).AddMany(other, 3 * calls);
        long allocated = await node.Counter(1).AddMany(other, calls);
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} calls");
    }

    [Test]
    public async Task KeyedEventsDoNotAllocate()
    {
        var node = await Start("alloc", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        long other = 2;
        while (await node.Counter(other).WhereAmI() == await node.Counter(1).WhereAmI()) other++;

        const int events = 20_000;
        await node.Counter(1).PublishMany(other, 3 * events);
        long allocated = await node.Counter(1).PublishMany(other, events);
        Assert.That(allocated / (double)events, Is.LessThan(8), $"{allocated} B for {events} events"); // see above
    }

    [Test]
    public async Task GatewayFanOutDoesNotAllocate()
    {
        var gateway = await Start("gw", gateway: true, configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        var receiver = new GoldCounter();
        const int members = 16;
        for (int i = 0; i < members; i++)
        {
            var client = await Connect(gateway);
            client.Register<IPlayerClient>(receiver);
            await client.Room(7).Join();
        }

        Assert.That(gateway.View.TryGetActorImplementation(RoomRpc.ContractId, out uint room));
        var group = gateway.PlayerClient(ClientGroup.Of(room, 7));

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
                group.OnGold(i);
                if (i % 64 == 63) // stay within the clients' send backlog
                    WaitFor(ref receiver.Received, (long)(i + 1) * members);
            }

            WaitFor(ref receiver.Received, (long)count * members);
            Interlocked.Exchange(ref receiver.Received, 0);
        }
    }

    [Test]
    public async Task ClientCallsThroughAGatewayToAnotherNodeDoNotAllocate()
    {
        var gateway = await Start("gw", gateway: true, hosts: static _ => false, configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        var game = await Start("game", configure: o => o.GossipInterval = TimeSpan.FromHours(1));
        await Converged();
        var client = await Connect(gateway);
        await client.Auth().Login(42); // binds the session to player 42 (on the game node)
        var player = client.Player();

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
                player.Tip(1);
                if (i % 500 == 0) await player.AddGold(0);
            }
        }
    }

    [Test]
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

    private sealed class GoldCounter : IPlayerClient
    {
        public long Received;

        public void OnChat(long from, string text)
        {
        }

        public void OnGold(int gold) => Interlocked.Increment(ref Received);
    }
}
