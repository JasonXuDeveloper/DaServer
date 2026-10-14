using System.Threading.Tasks;
using DaServer.Cluster;
using DaServer.Runtime;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Resumable node links (ADR-008): a dropped connection delays messages, it never loses or repeats them.</summary>
public class LinkTests : ClusterTest
{
    private static PeerLink LinkTo(Node from, Node to)
    {
        Assert.That(from.View.TryGetMember(to.Id, out var member), "members know each other");
        return from.GetLink(member!);
    }

    [Test]
    public async Task OneWayCallsSurviveDroppedConnections()
    {
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        long key = KeyOwnedBy(b, CounterRpc.ContractId);
        await a.Counter(key).Reset();
        var link = LinkTo(a, b);

        // Drop the connection several times while one-way calls stream through it.
        const int calls = 20_000;
        for (int i = 0; i < calls; i++)
        {
            a.Counter(key).Bump();
            if (i % 5_000 == 2_500) link.Connection?.Close();
        }

        await Eventually(async () => await a.Counter(key).Get() >= calls, "every call arrives");
        await Task.Delay(200);
        Assert.That(await a.Counter(key).Get(), Is.EqualTo(calls), "none ran twice");
    }

    [Test]
    public async Task CallsInFlightCompleteWhenTheReplyLinkDrops()
    {
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        long key = KeyOwnedBy(b, CounterRpc.ContractId);
        await a.Counter(key).Reset();
        var replies = LinkTo(b, a);

        // Replies travel on b's link to a: drop it (from a's side, the connection b opened) while calls are in flight.
        const int calls = 2_000;
        var pending = new ValueTask<int>[calls];
        for (int i = 0; i < calls; i++)
        {
            pending[i] = a.Counter(key).Add(1);
            if (i == calls / 2) LinkTo(a, b).Inbound?.Close();
        }

        for (int i = 0; i < calls; i++) await pending[i];
        Assert.That(await a.Counter(key).Get(), Is.EqualTo(calls));
        Assert.That(replies.IsConnected, Is.True, "the link resumed");
    }

    [Test]
    public async Task AStreamThatCannotResumeFailsItsCallsAndStartsOver()
    {
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        long key = KeyOwnedBy(b, CounterRpc.ContractId);
        await a.Counter(key).Reset();
        var link = LinkTo(a, b);

        // b forgets a's stream (as a restarted process would) once it acknowledged some of it: a's window cannot
        // replay from 0, so the stream starts over and the call in flight fails (Disconnected) instead of waiting.
        await Task.Delay(300);
        LinkTo(b, a).ForgetInboundStream();
        link.Connection?.Close();
        var lost = a.Counter(key).Add(1);
        Assert.That(async () => await lost, Throws.InstanceOf<DaServer.Rpc.RpcException>());
        await Eventually(async () =>
        {
            try
            {
                return await a.Counter(key).Add(0) >= 0;
            }
            catch (DaServer.Rpc.RpcException)
            {
                return false;
            }
        }, "the link starts a new stream");
    }
}
