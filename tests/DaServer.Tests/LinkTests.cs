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
        long key = KeyOwnedBy<CounterId>(b);
        await a.Ask(new CounterId(key), new Reset());
        var link = LinkTo(a, b);

        // Drop the connection several times while one-way calls stream through it.
        const int calls = 20_000;
        for (int i = 0; i < calls; i++)
        {
            a.Send(new CounterId(key), new Bump());
            if (i % 5_000 == 2_500) link.Connection?.Close();
        }

        await Eventually(async () => await a.Ask(new CounterId(key), new Get()) >= calls, "every call arrives");
        await Task.Delay(200);
        Assert.That(await a.Ask(new CounterId(key), new Get()), Is.EqualTo(calls), "none ran twice");
    }

    [Test]
    public async Task CallsInFlightCompleteWhenTheReplyLinkDrops()
    {
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        long key = KeyOwnedBy<CounterId>(b);
        await a.Ask(new CounterId(key), new Reset());
        var replies = LinkTo(b, a);

        // Replies travel on b's link to a: drop it (from a's side, the connection b opened) while calls are in flight.
        const int calls = 2_000;
        var pending = new Reply<int>[calls];
        for (int i = 0; i < calls; i++)
        {
            pending[i] = a.Ask(new CounterId(key), new Add(1));
            if (i == calls / 2) LinkTo(a, b).Inbound?.Close();
        }

        for (int i = 0; i < calls; i++) await pending[i];
        Assert.That(await a.Ask(new CounterId(key), new Get()), Is.EqualTo(calls));
        Assert.That(replies.IsConnected, Is.True, "the link resumed");
    }

    [Test]
    public async Task AStreamThatCannotResumeFailsItsCallsAndStartsOver()
    {
        var (a, b) = (await StartCluster(2)) is var n ? (n[0], n[1]) : default;
        long key = KeyOwnedBy<CounterId>(b);
        await a.Ask(new CounterId(key), new Reset());
        var link = LinkTo(a, b);

        // b forgets a's stream (as a restarted process would) once it acknowledged some of it: a's window cannot
        // replay from 0, so the stream starts over and the call in flight fails (Disconnected) instead of waiting. b's
        // shards are held so the call is surely in flight (sent, not answered) when the stream starts over.
        await Task.Delay(300);
        var gate = new ManualResetEventSlim(); // not disposed: a shard thread may still be inside Wait() when the test ends
        HoldShards(b, gate, 10);
        var lost = a.Ask(new CounterId(key), new Add(1)).AsTask();
        await Task.Delay(100); // it reached b
        LinkTo(b, a).ForgetInboundStream();
        link.Connection?.Close();
        var failure = Assert.ThrowsAsync<DaServer.Rpc.RpcException>(async () => await lost);
        Assert.That(failure!.Status, Is.EqualTo(DaServer.Protocol.RpcStatus.Disconnected));
        gate.Set();
        await Eventually(async () =>
        {
            try
            {
                return await a.Ask(new CounterId(key), new Add(0)) >= 0;
            }
            catch (DaServer.Rpc.RpcException)
            {
                return false;
            }
        }, "the link starts a new stream");
    }
}
