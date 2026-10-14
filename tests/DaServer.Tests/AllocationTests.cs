using System;
using System.Threading.Tasks;
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
}
