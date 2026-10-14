using System;
using System.Threading;
using System.Threading.Tasks;
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

        // Warm up past tiered-JIT promotion, then measure. The counter is process-wide, so background runtime work (the
        // maintenance timer, about 0.15 B per call at this rate) is included: anything per-call would be >= 24 B.
        const int calls = 20_000;
        await node.Counter(1).AddMany(other, 3 * calls);
        long allocated = await node.Counter(1).AddMany(other, calls);
        Assert.That(allocated / (double)calls, Is.LessThan(1), $"{allocated} B for {calls} calls");
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
        Assert.That(allocated / (double)events, Is.LessThan(1), $"{allocated} B for {events} events");
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
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Broadcast(broadcasts);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.That(allocated / (double)(broadcasts * members), Is.LessThan(1), $"{allocated} B for {broadcasts} broadcasts to {members}");

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
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await Calls(calls);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        // A burst can grow a pooled send buffer once (a few KB): anything per call would be >= 24 B.
        Assert.That(allocated / (double)calls, Is.LessThan(8), $"{allocated} B for {calls} client calls");

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
