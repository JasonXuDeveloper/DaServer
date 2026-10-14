using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Protocol;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Bounded queues: a flood is refused with <see cref="RpcStatus.Overloaded"/> instead of growing memory.</summary>
public class BackpressureTests : ClusterTest
{
    [Test]
    public async Task AFullActorMailboxRefusesNewCalls()
    {
        var node = await Start("single", shards: 1, configure: static o => o.ActorMailboxCapacity = 4);
        var counter = node.Counter(1);

        // Each call awaits 10 ms inside its turn, so the calls pile up behind the first one.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => StatusOf(counter.SlowAdd(1))));

        int ok = statuses.Count(s => s == RpcStatus.Ok), overloaded = statuses.Count(s => s == RpcStatus.Overloaded);
        Assert.That(ok + overloaded, Is.EqualTo(50), "every call either ran or was refused");
        Assert.That(ok, Is.InRange(5, 49), "the mailbox capacity and the running turn were used");
        Assert.That(await counter.Get(), Is.EqualTo(ok), "refused calls did not run");
        Assert.That(node.Rejected, Is.EqualTo(overloaded));
    }

    [Test]
    public async Task AFullShardRefusesNewCallsAndRecovers()
    {
        var node = await Start("single", shards: 1, configure: static o => o.MailboxCapacity = 16);
        var shard = node.Shards[0];
        using var gate = new ManualResetEventSlim();
        shard.Post(static g => ((ManualResetEventSlim)g!).Wait(), gate); // the shard is busy until the gate opens
        await Eventually(() => shard.QueueLength == 0, "the shard took the blocking callback");

        var calls = Enumerable.Range(0, 100).Select(_ => StatusOf(node.Counter(1).Add(1))).ToArray();
        int refused = calls.Count(c => c.IsCompleted);
        Assert.That(refused, Is.EqualTo(100 - 16), "calls beyond the capacity fail fast while the shard is busy");
        Assert.That(calls.Where(c => c.IsCompleted).All(c => c.Result == RpcStatus.Overloaded));

        gate.Set();
        var statuses = await Task.WhenAll(calls);
        Assert.That(statuses.Count(s => s == RpcStatus.Ok), Is.EqualTo(16));
        Assert.That(await node.Counter(1).Get(), Is.EqualTo(16), "admitted calls ran once the shard caught up");
        Assert.That(node.Rejected, Is.EqualTo(84));
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
