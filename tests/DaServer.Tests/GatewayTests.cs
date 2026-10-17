namespace DaServer.Tests;

/// <summary>Client sessions through gateways: access by contract marker, bindings, push, group broadcast, close.</summary>
public class GatewayTests : ClusterTest
{
    internal sealed class Inbox
    {
        public readonly ConcurrentQueue<string> Chat = new();
        public volatile int Gold;

        [Handle] public void On(Chat m) => Chat.Enqueue($"{m.From.Value}:{m.Text}");

        [Handle] public void On(GoldChanged m) => Gold = m.Gold;
    }

    [Test]
    public async Task SessionActorCallsReachTheKeyBoundToTheSession()
    {
        var gateway = await Start("gw", gateway: true);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        var inbox = new Inbox();
        client.Register(inbox);
        var observer = new Inbox(); // every registered handler of a push receives it
        client.Register(observer);

        var unbound = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new AddGold(1)));
        Assert.That(unbound!.Status, Is.EqualTo(RpcStatus.Forbidden), "session actors need a binding");

        Assert.That(await client.Ask(new Login(new PlayerId(42))), Is.True);
        Assert.That(await client.Ask(new AddGold(10)), Is.EqualTo(10));
        var named = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new PlayerId(999), new AddGold(5)));
        Assert.That(named!.Status, Is.EqualTo(RpcStatus.Forbidden), "a client names only actors granted to its session");
        Assert.That(await client.Ask(new PlayerId(42), new AddGold(0)), Is.EqualTo(10), "its bound actor is granted");
        Assert.That(await client.Ask(new AddGold(5)), Is.EqualTo(15));
        Assert.That(await gateway.Ask(new PlayerId(42), new AddGold(0)), Is.EqualTo(15));
        await Eventually(() => inbox.Gold == 15 && observer.Gold == 15, "push to the session was not delivered to both handlers");

        await client.Ask(new Logout());
        var unboundAgain = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new AddGold(1)));
        Assert.That(unboundAgain!.Status, Is.EqualTo(RpcStatus.Forbidden), "unbind removes the binding");
    }

    [Test]
    public async Task ABindingIsInPlaceWhenTheCallThatMadeItReturns()
    {
        // The binding is made on another node than the gateway, queued behind pushes to the same gateway: the login's
        // reply must not overtake it, or the client's next one-way call would be refused as unbound.
        var gateway = await Start("gw", gateway: true, hosts: static _ => false);
        await Start("game");
        await Converged();
        for (long player = 1; player <= 5; player++)
        {
            var client = await Connect(gateway);
            Assert.That(await client.Ask(new LoginAfterPushes(new PlayerId(player), 48)), Is.True);
            client.Send(new Tip(1));
            Assert.That(await client.Ask(new AddGold(0)), Is.EqualTo(1), $"the one-way call of player {player} was dropped");
        }
    }

    [Test]
    public async Task ABoundSessionReceivesTheActorsGroupBroadcasts()
    {
        var gateway = await Start("gw", gateway: true);
        var client = await Connect(gateway);
        var inbox = new Inbox();
        client.Register(inbox);
        await client.Ask(new Login(new PlayerId(42)));

        gateway.Send(ClientGroup.Of(new PlayerId(42)), new GoldChanged(7)); // the player's Group: joined by BindSession
        await Eventually(() => inbox.Gold == 7, "the bound session is not in the actor's group");
    }

    [Test]
    public async Task ActorsLearnThatTheSessionsOfACrashedGatewayAreGone()
    {
        var gateway = await Start("gw", gateway: true, hosts: static _ => false);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        await client.Ask(new Login(new PlayerId(77)));
        await client.Ask(new AddGold(1)); // the player knows the session from its call

        await gateway.KillAsync(); // no Leaving, no session close: found by failure detection only
        await Eventually(() => Observed.Events.Contains("closed:77"), "SessionClosed was not delivered after the gateway died");
    }

    [Test]
    public async Task BindingsAreIsolatedPerSession()
    {
        var gateway = await Start("gw", gateway: true);
        var first = await Connect(gateway);
        var second = await Connect(gateway);
        await first.Ask(new Login(new PlayerId(1)));
        await second.Ask(new Login(new PlayerId(2)));

        await first.Ask(new AddGold(100));
        Assert.That(await second.Ask(new AddGold(1)), Is.EqualTo(1), "each session reaches its own player only");
        Assert.That(await gateway.Ask(new PlayerId(1), new AddGold(0)), Is.EqualTo(100));
    }

    [Test]
    public async Task ClientsCannotCallServerOnlyContracts()
    {
        var client = await Connect(await Start("gw", gateway: true));
        var error = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new CounterId(1), new Get()));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Forbidden));
    }

    [Test]
    public async Task GroupBroadcastReachesMembersOnEveryGateway()
    {
        var gateways = new[] { await Start("gw1", gateway: true), await Start("gw2", gateway: true) };
        await Start("game");
        await Converged();

        var members = new Inbox[4];
        for (int i = 0; i < members.Length; i++)
        {
            var client = await Connect(gateways[i % 2]);
            client.Register(members[i] = new Inbox());
            await client.Ask(new Enter(new RoomId(7))); // the room grants the session: the client names it from now on
        }

        var outsider = await Connect(gateways[0]);
        var outsiderInbox = new Inbox();
        outsider.Register(outsiderInbox);
        await outsider.Ask(new Enter(new RoomId(8)));

        gateways[0].Send(new RoomId(7), new Say("hello"));
        await Eventually(() => members.All(m => m.Chat.Contains("7:hello")), "broadcast missing");
        await Task.Delay(100); // a stray delivery would arrive about as fast as the ones above
        Assert.That(outsiderInbox.Chat, Is.Empty, "groups of different keys are distinct");
    }

    [Test]
    public async Task ClientsNameOnlyActorsThatGrantedTheirSession()
    {
        var gateway = await Start("gw", gateway: true);
        var client = await Connect(gateway);
        var inbox = new Inbox();
        client.Register(inbox);
        var refused = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new RoomId(7), new Exit()));
        Assert.That(refused!.Status, Is.EqualTo(RpcStatus.Forbidden), "named without a grant");

        Assert.That(await client.Ask(new Enter(new RoomId(7))), Is.True);
        client.Send(new RoomId(7), new Say("hi"));
        await Eventually(() => inbox.Chat.Contains("7:hi"), "the granted room did not get the message, or its group missed the client");

        Assert.That(await client.Ask(new RoomId(7), new Exit()), Is.True, "the room revokes the grant");
        refused = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new RoomId(7), new Exit()));
        Assert.That(refused!.Status, Is.EqualTo(RpcStatus.Forbidden), "named after the revoke");
        gateway.Send(ClientGroup.Of(new RoomId(7)), new Chat(default, "after"));
        await Task.Delay(100); // a stray delivery would arrive about as fast
        Assert.That(inbox.Chat, Does.Not.Contain("0:after"), "the session left the room's group with its grant");
    }

    [Test]
    public async Task GroupsFollowJoinsAndClosesExactly()
    {
        var gateway = await Start("gw", gateway: true);
        var clients = new ClusterClient[30];
        var inboxes = new Inbox[clients.Length];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = await Connect(gateway);
            clients[i].Register(inboxes[i] = new Inbox());
            await clients[i].Ask(new Enter(new RoomId(7)));
        }

        // Close every third member (swap-remove moves others into their slots), then broadcast to the room and to All.
        for (int i = 0; i < clients.Length; i += 3) await clients[i].DisposeAsync();
        await Eventually(() => gateway.SessionCount() == 20, "sessions did not close");
        gateway.Send(new RoomId(7), new Say("hello"));
        gateway.Send(ClientGroup.All, new Chat(default, "all"));

        var open = Enumerable.Range(0, clients.Length).Where(i => i % 3 != 0).Select(i => inboxes[i]).ToArray();
        await Eventually(() => open.All(m => m.Chat.Count == 2), "a member missed a broadcast");
        await Task.Delay(100); // a duplicate would arrive about as fast
        Assert.That(open.All(m => m.Chat.Order().SequenceEqual(["0:all", "7:hello"])), "each member got each broadcast once");
    }

    [Test]
    public async Task SessionOperationsSentToAGroupApplyToEveryMember()
    {
        // Two gateways: room 7's members on both join room 9's group, then room 9 is disconnected as a whole.
        var gateways = new[] { await Start("gw-a", gateway: true), await Start("gw-b", gateway: true) };
        await Converged();
        var members = new ClusterClient[4];
        var inboxes = new Inbox[members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            members[i] = await Connect(gateways[i % 2]);
            members[i].Register(inboxes[i] = new Inbox());
            await members[i].Ask(new Enter(new RoomId(7)));
        }

        var room9 = ClientGroup.Of(new RoomId(9));
        gateways[0].Send(ClientGroup.Of(new RoomId(7)), new JoinGroup(room9));
        await Task.Delay(200); // applied on both gateways
        gateways[1].Send(room9, new Chat(default, "nine"));
        await Eventually(() => inboxes.All(m => m.Chat.Contains("0:nine")), "a member did not join the group");

        gateways[0].Send(room9, new Disconnect());
        await Eventually(() => gateways.Sum(g => g.SessionCount()) == 0, "the group's sessions were not closed");
    }

    [Test]
    public async Task ClientsOverTheirMessageRateAreRefusedWithARetryHint()
    {
        var gateway = await Start("gw", clients: static o => (o.MessagesPerSecond, o.Burst) = (1, 2));
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        Assert.That(await client.Ask(new Login(new PlayerId(9))), Is.True);
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "within the burst");
        var refused = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new AddGold(1)));
        Assert.That(refused!.Status, Is.EqualTo(RpcStatus.RateLimited));
        Assert.That(refused.RetryAfter, Is.GreaterThan(TimeSpan.Zero).And.LessThanOrEqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task ClientsBoundTheirCallsInFlight()
    {
        Observed.Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = await Start("gw", gateway: true);
        var client = Track(new ClusterClient(maxPendingCalls: 16));
        await client.ConnectAsync("127.0.0.1", gateway.ClientPort());

        var waiting = Enumerable.Range(0, 16).Select(_ => client.Ask(new Stall()).AsTask()).ToArray();
        var refused = Assert.ThrowsAsync<RpcException>(async () => await client.Ask(new Stall()));
        Assert.That(refused!.Status, Is.EqualTo(RpcStatus.Overloaded), "one call more than the table holds");

        Observed.Release.SetResult(true);
        Assert.That(await Task.WhenAll(waiting), Is.All.True);
        Assert.That(await client.Ask(new Stall()), Is.True, "the table has room again");
    }

    [Test]
    public async Task AnOversizedClientFrameClosesTheSession()
    {
        var gateway = await Start("gw", clients: static o => o.MaxFrameSize = 1024);
        var client = await Connect(gateway);
        await client.Ask(new Enter(new RoomId(7)));
        client.Send(new RoomId(7), new Say(new string('x', 2048)));
        await Eventually(() => gateway.SessionCount() == 0, "a frame over MaxFrameSize was accepted");
    }

    [Test]
    public async Task ClosingASessionNotifiesTheBoundActors()
    {
        var gateway = await Start("gw", gateway: true);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        await client.Ask(new Login(new PlayerId(77)));

        await client.DisposeAsync();
        await Eventually(() => Observed.Events.Contains("closed:77"), "SessionClosed not delivered to the bound player");
    }
}
