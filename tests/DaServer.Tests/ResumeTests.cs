using System.Net;
using System.Text;

namespace DaServer.Tests;

/// <summary>Resume tickets: a client that lost its connection resumes its session; actors see no close.</summary>
public class ResumeTests : ClusterTest
{
    private static Action<NodeOptions> Secret(TimeSpan grace) => o =>
    {
        o.SigningSecret = "test-secret";
        o.ResumeGrace = grace;
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

    /// <summary>A call retried while it meets a connection closed for its age (what an application does on <c>Disconnected</c>).</summary>
    private static async Task<T> Retried<T>(Func<ValueTask<T>> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (RpcException e) when (e.Status == RpcStatus.Disconnected && attempt < 20)
            {
                await Task.Delay(100);
            }
        }
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
        Assert.That(Observed.Events, Does.Contain("resumed:501"));
        gateways[0].Send(ClientGroup.Of(new PlayerId(501)), new GoldChanged(9));
        await Eventually(() => inbox.Gold == 9, "the new session is in the actor's group");

        await Task.Delay(1500); // past the grace: the lost session's close must not reach the player
        Assert.That(Observed.Events, Does.Not.Contain("closed:501"));
    }

    [Test]
    public async Task ClientsOfALeavingGatewayMoveAndResumeOnAnother()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(1));
        var client = await LogIn(gateways, 510);
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 1, "the entry connection was not closed");
        var leaving = gateways.Single(g => g.SessionCount() == 1);

        var reconnected = Reconnected(client);
        await Stop(leaving); // graceful: listener closed, connections closed in waves
        await reconnected;

        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the session resumed on the other node");
        Assert.That(gateways.Single(g => g != leaving).SessionCount(), Is.EqualTo(1));
        await Task.Delay(3000); // the leaving node is gone and the grace passed: the resumed session is not reported closed
        Assert.That(Observed.Events, Does.Not.Contain("closed:510"));
    }

    [Test]
    public async Task OldConnectionsMoveToANodeStartedLater()
    {
        // The clients' entry addresses stand for a load balancer: the second one has no node behind it yet.
        int later;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            later = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        static void Aged(ClientConnectionOptions o) => o.MaxConnectionAge = TimeSpan.FromSeconds(3); // past the logins and their tickets
        var first = await Start("gw-0", gateway: true, hosts: static _ => false, configure: Secret(TimeSpan.FromSeconds(5)), clients: Aged);
        await Start("game", configure: Secret(TimeSpan.FromSeconds(5)));
        await Converged();
        var clients = new ClusterClient[20];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = Track(new ClusterClient());
            await clients[i].ConnectAsync([new DnsEndPoint("127.0.0.1", first.ClientPort()), new DnsEndPoint("127.0.0.1", later)]);
            var client = clients[i];
            long player = 600 + i;
            Assert.That(await Retried(() => client.Ask(new Login(new PlayerId(player))).AsValueTask()), Is.True);
        }

        // Scale out: connections older than their age close, and their clients reconnect through the entry addresses.
        var second = await Start("gw-1", gateway: true, hosts: static _ => false, configure: Secret(TimeSpan.FromSeconds(5)),
            clients: o => { o.Port = later; Aged(o); });
        await Converged();
        await Eventually(() => second.SessionCount() >= 5, "no connection moved to the new node", 15_000);
        foreach (var client in clients)
            Assert.That(await Retried(() => client.Ask(new AddGold(1)).AsValueTask()), Is.GreaterThan(0), "every session resumed where it reconnected");
        Assert.That(Observed.Events.Where(e => e.StartsWith("closed:6", StringComparison.Ordinal)), Is.Empty, "no player saw a close");
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
    public async Task GrantsMoveToTheResumedSessionAndClosesReachGrantedActors()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(1));
        var client = await LogIn(gateways, 505);
        Assert.That(await client.Ask(new Enter(new RoomId(70))), Is.True);

        var reconnected = Reconnected(client);
        client.DropConnection();
        await reconnected;
        await Eventually(() => Observed.Events.Contains("room-resumed:70"), "the granted room did not learn the new session");
        Assert.That(await client.Ask(new RoomId(70), new Exit()), Is.True, "the grant moved to the new session");

        var other = await LogIn(gateways, 506);
        Assert.That(await other.Ask(new Enter(new RoomId(71))), Is.True);
        await other.DisposeAsync();
        await Eventually(() => Observed.Events.Contains("room-closed:71"), "the granted room did not learn the close");
    }

    /// <summary>A second client presents <paramref name="ticket"/>: the status it gets (Ok: resumed).</summary>
    private async Task<RpcStatus> ResumeAs(Node[] gateways, byte[] ticket) => (await ResumeWith(gateways, ticket)).Status;

    private async Task<(RpcStatus Status, ClusterClient Client)> ResumeWith(Node[] gateways, byte[] ticket)
    {
        var client = Track(new ClusterClient { AutoReconnect = false });
        await client.ConnectAsync("127.0.0.1", gateways[1].ClientPort());
        try
        {
            await client.ResumeWith(ticket);
            return (RpcStatus.Ok, client);
        }
        catch (RpcException e)
        {
            return (e.Status, client);
        }
    }

    [Test]
    public async Task ATicketResumesOnce()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 507);
        var ticket = client.Ticket!;
        var reconnected = Reconnected(client);
        client.DropConnection();
        await reconnected;

        Assert.That(await ResumeAs(gateways, ticket), Is.EqualTo(RpcStatus.Forbidden), "a replayed ticket resumed again");
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the resumed client kept its session");
    }

    [Test]
    public async Task AWithdrawnGrantDoesNotComeBackWithAnOlderTicket()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 508);
        Assert.That(await client.Ask(new Enter(new RoomId(72))), Is.True);
        await Eventually(() => client.Ticket is { Length: > 0 } t && t.Length > ResumeTicket.SizeOf(1), "no ticket with the room");
        var older = client.Ticket!;
        Assert.That(await client.Ask(new RoomId(72), new Exit()), Is.True); // the room revokes the grant
        client.AutoReconnect = false;
        client.DropConnection();

        var (status, resumed) = await ResumeWith(gateways, older);
        Assert.That(status, Is.EqualTo(RpcStatus.Ok), "the player binding is still held");
        var exit = Assert.ThrowsAsync<RpcException>(async () => await resumed.Ask(new RoomId(72), new Exit()));
        Assert.That(exit!.Status, Is.EqualTo(RpcStatus.Forbidden), "the revoked grant came back");
        Assert.That(Observed.Events, Does.Not.Contain("room-resumed:72"), "the room took a session it had revoked");
    }

    [Test]
    public async Task NothingResumesAfterTheGrace()
    {
        var gateways = await StartCluster(TimeSpan.FromMilliseconds(300));
        var client = await LogIn(gateways, 509);
        var ticket = client.Ticket!;
        client.AutoReconnect = false;
        client.DropConnection();
        await Eventually(() => Observed.Events.Contains("closed:509"), "the close was not reported");

        Assert.That(await ResumeAs(gateways, ticket), Is.EqualTo(RpcStatus.Forbidden), "a closed session resumed");
    }

    [Test]
    public void TicketsAreUnforgeableAndExpire()
    {
        byte[] secret = Encoding.UTF8.GetBytes("secret");
        var session = new ClientSession(7, 42);
        SessionGrant[] grants = [new(1, 100, Bound: true), new(2, 200, Bound: false)];
        var ticket = new byte[ResumeTicket.SizeOf(grants.Length)];
        ResumeTicket.Write(ticket, secret, session, grants, nowMs: 1000, lifetimeMs: 500);

        Assert.That(ResumeTicket.TryRead(ticket, secret, 1200, out var read, out var readGrants), Is.True);
        Assert.That(read, Is.EqualTo(session));
        Assert.That(ResumeTicket.GrantCount(readGrants), Is.EqualTo(2));
        Assert.That(ResumeTicket.GrantAt(readGrants, 0), Is.EqualTo(grants[0]));
        Assert.That(ResumeTicket.GrantAt(readGrants, 1), Is.EqualTo(grants[1]));

        Assert.That(ResumeTicket.TryRead(ticket, secret, 1500, out _, out _), Is.False, "expired");
        Assert.That(ResumeTicket.TryRead(ticket, Encoding.UTF8.GetBytes("other"), 1200, out _, out _), Is.False, "another cluster");
        ticket[30] ^= 1; // another session
        Assert.That(ResumeTicket.TryRead(ticket, secret, 1200, out _, out _), Is.False, "tampered");
        Assert.That(ResumeTicket.TryRead(ticket.AsSpan(0, 20), secret, 1200, out _, out _), Is.False, "truncated");
    }
}
