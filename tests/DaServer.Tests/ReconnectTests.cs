using System.Net;
using System.Text;

namespace DaServer.Tests;

/// <summary>Reconnect tickets: a client that lost its connection resumes its session; actors see no close.</summary>
public class ReconnectTests : ClusterTest
{
    private static Action<NodeOptions> Secret(TimeSpan grace) => o =>
    {
        o.ClusterSecret = "test-secret";
        o.ReconnectGrace = grace;
    };

    private async Task<Node[]> StartCluster(TimeSpan grace)
    {
        var gateways = new[]
        {
            await Start("gw-0", gateway: true, hosts: static _ => false, configure: Secret(grace)),
            await Start("gw-1", gateway: true, hosts: static _ => false, configure: Secret(grace)),
        };
        await Start("game", configure: Secret(grace));
        await Converged();
        return gateways;
    }

    /// <summary>Log in through the client-facing nodes' addresses (the client reconnects through them too).</summary>
    private async Task<ClusterClient> LogIn(Node[] gateways, long player)
    {
        var client = Track(new ClusterClient());
        await client.ConnectAsync([.. gateways.Select(static g => new DnsEndPoint("127.0.0.1", g.ClientPort()))]);
        Assert.That(await client.Ask(new Login(new PlayerId(player))), Is.True);
        return client;
    }

    private static Task Reconnected(ClusterClient client)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Reconnected += () => done.TrySetResult();
        client.ReconnectFailed += e => done.TrySetException(e ?? new Exception("reconnect failed"));
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ALostConnectionResumesTheSession()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(1));
        var client = await LogIn(gateways, 501);
        var inbox = new GatewayTests.Inbox();
        client.Register(inbox);
        Assert.That(await client.Ask(new AddGold(5)), Is.EqualTo(5));

        var reconnected = Reconnected(client);
        client.DropConnection();
        await reconnected;

        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(6), "the binding moved to the new session");
        Assert.That(Observed.Events, Does.Contain("resync:501"));
        gateways[0].Send(ClientGroup.Of(new PlayerId(501)), new GoldChanged(9));
        await Eventually(() => inbox.Gold == 9, "the new session is in the actor's group");

        await Task.Delay(1500); // past the grace: the lost session's close must not reach the player
        Assert.That(Observed.Events, Does.Not.Contain("closed:501"));
    }

    [Test]
    public async Task ClientsOfACrashedGatewayResumeOnAnother()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(2));
        var client = await LogIn(gateways, 502);
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 1, "the entry connection was not closed");
        var crashed = gateways.Single(g => g.SessionCount() == 1);

        var reconnected = Reconnected(client);
        await crashed.KillAsync();
        await reconnected;

        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1));
        Assert.That(gateways.Single(g => g != crashed).SessionCount(), Is.EqualTo(1));
        await Task.Delay(4000); // failure detection (1.5 s) plus the grace
        Assert.That(Observed.Events, Does.Not.Contain("closed:502"), "the dead gateway's session was resumed");
    }

    [Test]
    public async Task AClientThatDoesNotComeBackIsReportedClosedAfterTheGrace()
    {
        var gateways = await StartCluster(TimeSpan.FromMilliseconds(800));
        var client = await LogIn(gateways, 503);
        client.AutoReconnect = false;

        client.DropConnection();
        await Task.Delay(300);
        Assert.That(Observed.Events, Does.Not.Contain("closed:503"), "reported before the grace ended");
        await Eventually(() => Observed.Events.Contains("closed:503"), "SessionClosed was not delivered after the grace");
    }

    [Test]
    public async Task ClosingOnPurposeIsReportedAtOnce()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(30));
        var client = await LogIn(gateways, 504);
        await client.DisposeAsync();
        await Eventually(() => Observed.Events.Contains("closed:504"), "an explicit close waited for the grace");
    }

    [Test]
    public void TicketsAreUnforgeableAndExpire()
    {
        byte[] secret = Encoding.UTF8.GetBytes("secret");
        var session = new SessionRef(7, 42);
        SessionBinding[] bindings = [new(1, 100), new(2, 200)];
        var ticket = new byte[ReconnectTicket.SizeOf(bindings.Length)];
        ReconnectTicket.Write(ticket, secret, session, bindings, nowMs: 1000, lifetimeMs: 500);

        Assert.That(ReconnectTicket.TryRead(ticket, secret, 1200, out var read, out var readBindings), Is.True);
        Assert.That(read, Is.EqualTo(session));
        Assert.That(ReconnectTicket.BindingCount(readBindings), Is.EqualTo(2));
        Assert.That(ReconnectTicket.BindingAt(readBindings, 1), Is.EqualTo(bindings[1]));

        Assert.That(ReconnectTicket.TryRead(ticket, secret, 1500, out _, out _), Is.False, "expired");
        Assert.That(ReconnectTicket.TryRead(ticket, Encoding.UTF8.GetBytes("other"), 1200, out _, out _), Is.False, "another cluster");
        ticket[30] ^= 1; // another session
        Assert.That(ReconnectTicket.TryRead(ticket, secret, 1200, out _, out _), Is.False, "tampered");
        Assert.That(ReconnectTicket.TryRead(ticket.AsSpan(0, 20), secret, 1200, out _, out _), Is.False, "truncated");
    }
}
