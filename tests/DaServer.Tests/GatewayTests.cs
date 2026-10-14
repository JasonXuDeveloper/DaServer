using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Client;
using DaServer.Protocol;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Client sessions through gateways: access by contract marker, bindings, push, group broadcast, close.</summary>
public class GatewayTests : ClusterTest
{
    private sealed class Inbox : IPlayerClient
    {
        public readonly ConcurrentQueue<string> Chat = new();
        public volatile int Gold;

        public void OnChat(long from, string text) => Chat.Enqueue($"{from}:{text}");

        public void OnGold(int gold) => Gold = gold;
    }

    [Test]
    public async Task SessionActorCallsReachTheKeyBoundToTheSession()
    {
        var gateway = await Start("gw", gateway: true);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        var inbox = new Inbox();
        client.Register<IPlayerClient>(inbox);

        var unbound = Assert.ThrowsAsync<RpcException>(async () => await client.Player().AddGold(1));
        Assert.That(unbound!.Status, Is.EqualTo(RpcStatus.Forbidden), "session actors need a binding");

        Assert.That(await client.Auth().Login(42), Is.True);
        Assert.That(await client.Player().AddGold(10), Is.EqualTo(10));
        Assert.That(await client.Player(999).AddGold(5), Is.EqualTo(15), "a key chosen by the client is ignored");
        Assert.That(await gateway.Player(42).AddGold(0), Is.EqualTo(15));
        await Eventually(() => inbox.Gold == 15, "push to the session was not delivered");

        await client.Auth().Logout();
        var unboundAgain = Assert.ThrowsAsync<RpcException>(async () => await client.Player().AddGold(1));
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
            Assert.That(await client.Auth().LoginAfterPushes(player, pushes: 48), Is.True);
            client.Player().Tip(1);
            Assert.That(await client.Player().AddGold(0), Is.EqualTo(1), $"the one-way call of player {player} was dropped");
        }
    }

    [Test]
    public async Task ABoundSessionReceivesTheActorsGroupBroadcasts()
    {
        var gateway = await Start("gw", gateway: true);
        var client = await Connect(gateway);
        var inbox = new Inbox();
        client.Register<IPlayerClient>(inbox);
        await client.Auth().Login(42);

        Assert.That(gateway.View.TryGetActorImplementation(PlayerRpc.ContractId, out uint player));
        gateway.PlayerClient(ClientGroup.Of(player, 42)).OnGold(7); // the player's Group: joined by Bind, no JoinGroup
        await Eventually(() => inbox.Gold == 7, "the bound session is not in the actor's group");
    }

    [Test]
    public async Task ActorsLearnThatTheSessionsOfACrashedGatewayAreGone()
    {
        var gateway = await Start("gw", gateway: true, hosts: static _ => false);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        await client.Auth().Login(77);
        await client.Player().AddGold(1); // the player knows the session from its call

        await gateway.KillAsync(); // no Leaving, no session close: found by failure detection only
        await Eventually(() => Observed.Events.Contains("closed:77"), "SessionClosed was not delivered after the gateway died");
    }

    [Test]
    public async Task BindingsAreIsolatedPerSession()
    {
        var gateway = await Start("gw", gateway: true);
        var first = await Connect(gateway);
        var second = await Connect(gateway);
        await first.Auth().Login(1);
        await second.Auth().Login(2);

        await first.Player().AddGold(100);
        Assert.That(await second.Player().AddGold(1), Is.EqualTo(1), "each session reaches its own player only");
        Assert.That(await gateway.Player(1).AddGold(0), Is.EqualTo(100));
    }

    [Test]
    public async Task ClientsCannotCallServerOnlyContracts()
    {
        var client = await Connect(await Start("gw", gateway: true));
        var error = Assert.ThrowsAsync<RpcException>(async () => await client.Counter(1).Get());
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
            client.Register<IPlayerClient>(members[i] = new Inbox());
            await client.Room(7).Join(); // public actor: the client chooses the room
        }

        var outsider = await Connect(gateways[0]);
        var outsiderInbox = new Inbox();
        outsider.Register<IPlayerClient>(outsiderInbox);
        await outsider.Room(8).Join();

        gateways[0].Room(7).Say("hello");
        await Eventually(() => members.All(m => m.Chat.Contains("7:hello")), "broadcast missing");
        await Task.Delay(100); // a stray delivery would arrive about as fast as the ones above
        Assert.That(outsiderInbox.Chat, Is.Empty, "groups of different keys are distinct");
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
            clients[i].Register<IPlayerClient>(inboxes[i] = new Inbox());
            await clients[i].Room(7).Join();
        }

        // Close every third member (swap-remove moves others into their slots), then broadcast to the room and to All.
        for (int i = 0; i < clients.Length; i += 3) await clients[i].DisposeAsync();
        await Eventually(() => gateway.SessionCount == 20, "sessions did not close");
        gateway.Room(7).Say("hello");
        gateway.PlayerClient(ClientGroup.All).OnChat(0, "all");

        var open = Enumerable.Range(0, clients.Length).Where(i => i % 3 != 0).Select(i => inboxes[i]).ToArray();
        await Eventually(() => open.All(m => m.Chat.Count == 2), "a member missed a broadcast");
        await Task.Delay(100); // a duplicate would arrive about as fast
        Assert.That(open.All(m => m.Chat.Order().SequenceEqual(["0:all", "7:hello"])), "each member got each broadcast once");
    }

    [Test]
    public async Task AnOversizedClientFrameClosesTheSession()
    {
        var gateway = await Start("gw", gateway: true, configure: static o => o.MaxClientFrameSize = 1024);
        var client = await Connect(gateway);
        await client.Room(7).Join();
        client.Room(7).Say(new string('x', 2048));
        await Eventually(() => gateway.SessionCount == 0, "a frame over MaxClientFrameSize was accepted");
    }

    [Test]
    public async Task ABalancerSpreadsClientsOverTheOtherGateways()
    {
        var balancer = await Start("balancer", gateway: true, hosts: static _ => false, configure: static o => o.Balancer = true);
        var gateways = new[]
        {
            await Start("gw-0", gateway: true, hosts: static _ => false),
            await Start("gw-1", gateway: true, hosts: static _ => false),
        };
        await Start("game");
        await Converged();

        var clients = new ClusterClient[40];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = Track(new ClusterClient());
            await clients[i].ConnectAsync("127.0.0.1", balancer.ClientPort); // the one well-known address
            Assert.That(await clients[i].Auth().Login(1000 + i), Is.True);
        }

        await Eventually(() => balancer.SessionCount == 0, "the balancer kept sessions");
        Assert.That(gateways.Sum(g => g.SessionCount), Is.EqualTo(clients.Length));
        Assert.That(gateways.Min(g => g.SessionCount), Is.GreaterThanOrEqualTo(8), "power of two choices spreads the load");
    }

    [Test]
    public async Task AGatewayStartedLaterIsDiscoveredAndReceivesTheNewClients()
    {
        var balancer = await Start("balancer", gateway: true, hosts: static _ => false, configure: static o => o.Balancer = true);
        var first = await Start("gw-0", gateway: true, hosts: static _ => false);
        await Start("game");
        await Converged();

        const int wave = 30;
        var clients = new ClusterClient[2 * wave];
        for (int i = 0; i < wave; i++) clients[i] = await ConnectThrough(balancer, 2000 + i);
        Assert.That(first.SessionCount, Is.EqualTo(wave));

        // Scale out: nobody configures the balancer; gossip tells it about the new gateway.
        var late = await Start("gw-1", gateway: true, hosts: static _ => false);
        await Converged();
        for (int i = wave; i < clients.Length; i++) clients[i] = await ConnectThrough(balancer, 2000 + i);

        Assert.That(late.SessionCount, Is.GreaterThanOrEqualTo(wave - 2), "new clients go to the least loaded gateway");
        Assert.That(first.SessionCount + late.SessionCount, Is.EqualTo(clients.Length));
    }

    private async Task<ClusterClient> ConnectThrough(DaServer.Runtime.Node balancer, long player)
    {
        var client = Track(new ClusterClient());
        await client.ConnectAsync("127.0.0.1", balancer.ClientPort);
        Assert.That(await client.Auth().Login(player), Is.True);
        return client;
    }

    [Test]
    public async Task ClientsConnectingWhileTheClusterFormsWaitForAGateway()
    {
        var balancer = await Start("balancer", gateway: true, hosts: static _ => false, configure: static o => o.Balancer = true);
        var client = Track(new ClusterClient());
        var connecting = client.ConnectAsync("127.0.0.1", balancer.ClientPort).AsTask(); // no gateway exists yet

        await Task.Delay(300);
        var gateway = await Start("gw", gateway: true);
        await connecting;
        // The client's connect completes before the gateway's accept loop has registered the session.
        await Eventually(() => gateway.SessionCount == 1, "the client was directed to the gateway once it joined");
        Assert.That(await client.Auth().Login(1), Is.True);
    }

    [Test]
    public async Task ClosingASessionNotifiesTheBoundActors()
    {
        var gateway = await Start("gw", gateway: true);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        await client.Auth().Login(77);

        await client.DisposeAsync();
        await Eventually(() => Observed.Events.Contains("closed:77"), "SessionClosed not delivered to the bound player");
    }
}
