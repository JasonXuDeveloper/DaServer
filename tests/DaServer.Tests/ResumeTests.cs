using System.Net;

namespace DaServer.Tests;

/// <summary>Resume tickets: a client that lost its connection resumes its session; actors see no close.</summary>
public partial class ResumeTests : ClusterTest
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
    public async Task AClientResumesWithAnActorThatMovedToAnotherNode()
    {
        // The actor took its sessions along (the handoff record's sessions section): the client resumes, no login.
        var gateways = await StartCluster(TimeSpan.FromSeconds(1));
        var clients = new Dictionary<long, ClusterClient>();
        for (long player = 530; player < 550; player++) clients[player] = await LogIn(gateways, player);
        var second = await Start("game-2", configure: Secret(TimeSpan.FromSeconds(1)));
        await Converged();
        var moved = clients.Keys.Where(p => second.OwnerOf<PlayerId>(p) == second.Id).ToArray();
        Assert.That(moved, Is.Not.Empty);

        foreach (long player in moved)
        {
            var client = clients[player];
            int gold = await client.Ask(new AddGold(1)); // reaches the player on its new node
            var reconnected = Reconnected(client);
            client.DropConnection();
            await reconnected;
            Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(gold + 1), "the moved player took the resumed session");
            Assert.That(Observed.Events, Does.Contain("resumed:" + player));
        }

        await Task.Delay(1500); // past the grace: no lost session's close reaches a moved player
        foreach (long player in moved) Assert.That(Observed.Events, Does.Not.Contain("closed:" + player));
    }

    [Test]
    public async Task AClientFacingNodeFalselyDeclaredDeadKeepsItsSessions()
    {
        // The game node heard gw-0 declared dead (a pause, a blip), then gw-0 refuted at a higher incarnation. Its clients
        // never disconnected, so they never resume: their sessions must not be closed when the grace runs out.
        var gateways = await StartCluster(TimeSpan.FromSeconds(1));
        var client = Track(new ClusterClient());
        await client.ConnectAsync("127.0.0.1", gateways[0].ClientPort());
        Assert.That(await client.Ask(new Login(new PlayerId(560))), Is.True);
        var game = Nodes.Single(n => n.Options.Name == "game");

        static byte[] Card(ClusterMember card) => Nino.Core.NinoSerializer.Serialize(new[] { card });
        var dead = gateways[0].Membership.Self.Clone();
        dead.Status = MemberStatus.Dead;
        game.Membership.OnGossip(null!, ControlOp.Cards, Card(dead));
        var back = gateways[0].Membership.Self.Clone();
        back.Incarnation = dead.Incarnation + 1;
        game.Membership.OnGossip(null!, ControlOp.Cards, Card(back));

        await Task.Delay(1500); // past the grace
        Assert.That(Observed.Events, Does.Not.Contain("closed:560"));
        Assert.That(await client.Ask(new AddGold(1)), Is.GreaterThan(0), "the session still reaches its player");
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
    public async Task ASubscriberThatThrowsOnDisconnectedDoesNotStopTheReconnect()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 531);
        client.Disconnected += static _ => throw new InvalidOperationException("a subscriber's bug");
        int reported = 0;
        client.HandlerFailed += _ => Interlocked.Increment(ref reported);
        var reconnected = Reconnected(client);
        client.DropConnection();
        await reconnected;
        Assert.That(Volatile.Read(ref reported), Is.EqualTo(1), "the subscriber's failure was reported");
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the session resumed");
    }

    private static Task Moved(ClusterClient client)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Moved += () => done.TrySetResult();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ClientsOfALeavingGatewayMoveAndResumeOnAnother()
    {
        var gateways = await StartCluster(TimeSpan.FromSeconds(1));
        var client = await LogIn(gateways, 510);
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 1, "the entry connection was not closed");
        var leaving = gateways.Single(g => g.SessionCount() == 1);

        var moved = Moved(client);
        await Stop(leaving); // graceful: listener closed, clients retired in waves, the stop waits until they left
        await moved;

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
    public async Task AConnectionRetiredForItsAgeLosesNoPush()
    {
        // Make before break: the client resumes on a new connection while the old one keeps working, so every call
        // succeeds and every push it causes arrives, across several moves.
        static void Aged(ClientConnectionOptions o) => o.MaxConnectionAge = TimeSpan.FromSeconds(2);
        var gateways = new[]
        {
            await Start("gw-0", gateway: true, hosts: static _ => false, configure: Secret(TimeSpan.FromSeconds(5)), clients: Aged),
            await Start("gw-1", gateway: true, hosts: static _ => false, configure: Secret(TimeSpan.FromSeconds(5)), clients: Aged),
        };
        await Start("game", configure: Secret(TimeSpan.FromSeconds(5)));
        await Converged();
        var client = await LogIn(gateways, 520);
        var pushes = new PushCount();
        client.Register(pushes);
        int moves = 0;
        client.Moved += () => Interlocked.Increment(ref moves);

        int replies = 0;
        var failures = new List<string>();
        var until = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < until)
        {
            var calls = new Task<int>[20];
            for (int i = 0; i < calls.Length; i++) calls[i] = client.Ask(new AddGold(1)).AsTask();
            foreach (var call in calls)
            {
                try
                {
                    await call;
                    replies++;
                }
                catch (RpcException e)
                {
                    failures.Add(e.Status.ToString());
                }
            }
        }

        Assert.That(failures, Is.Empty, "no call fails across the moves");
        Assert.That(moves, Is.GreaterThanOrEqualTo(2), "the connection was retired for its age");
        await Eventually(() => pushes.Count == replies, $"pushes lost: {replies - pushes.Count} of {replies}");
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 1, "the old connections were left");
        Assert.That(Observed.Events, Does.Not.Contain("closed:520"));
    }

    [Test]
    public async Task AMoveLeavesItsOldConnectionOnceEveryActorMarkedIt()
    {
        // The node answers a resume with how many actors took the session, and each sends SessionMoved on the old one:
        // the counts agree, so the client says goodbye at once instead of after its (long) call timeout.
        static void Aged(ClientConnectionOptions o) => o.MaxConnectionAge = TimeSpan.FromSeconds(2);
        var gateways = new[]
        {
            await Start("gw-0", gateway: true, hosts: static _ => false, configure: Secret(TimeSpan.FromSeconds(5)), clients: Aged),
            await Start("gw-1", gateway: true, hosts: static _ => false, configure: Secret(TimeSpan.FromSeconds(5)), clients: Aged),
        };
        await Start("game", configure: Secret(TimeSpan.FromSeconds(5)));
        await Converged();
        var client = await LogIn(gateways, 530);
        Assert.That(await client.Ask(new Enter(new RoomId(79))), Is.True, "a second actor holds the session");
        client.CallTimeout = TimeSpan.FromSeconds(30);
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 1, "the entry connection was not closed");

        await Moved(client);
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 1, "the old connection waited for a marker that never came", timeoutMs: 5_000);
    }

    internal sealed class PushCount
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        [Handle] public void On(GoldChanged _) => Interlocked.Increment(ref _count);
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

    [Test]
    public async Task AResumeCutOffAfterTheActorsAcceptedItIsRetried()
    {
        // The player holds a turn while its client reconnects: each resume waits past the client's call timeout and the
        // client drops it, so the player accepts an attempt whose connection is gone. That attempt is reverted, and the
        // client's next one resumes the session instead of being refused for good.
        var gateways = await StartCluster(TimeSpan.FromSeconds(5));
        var client = await LogIn(gateways, 596);
        client.CallTimeout = TimeSpan.FromMilliseconds(300);
        TurnGate.Current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = client.Ask(new HoldPlayer()).AsTask();
        await Task.Delay(100);
        var reconnected = Reconnected(client);
        client.DropConnection();
        await Task.Delay(1_000); // attempts time out behind the held turn
        TurnGate.Current.TrySetResult();

        await reconnected;
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the session resumed with the player");
        Assert.That(Observed.Events, Does.Not.Contain("closed:596"));
        try { await held; } catch (RpcException) { } // its connection was dropped
    }
}
