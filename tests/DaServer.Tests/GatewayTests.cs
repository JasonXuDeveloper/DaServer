using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
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
