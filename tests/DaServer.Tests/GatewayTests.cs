using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Protocol;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Client sessions through gateways: binding, access control, push and group broadcast.</summary>
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
    public async Task CallsReachTheActorBoundToTheSession()
    {
        var gateway = await Start("gw", gateway: true);
        await Start("game");
        await Converged();
        var client = await Connect(gateway);
        var inbox = new Inbox();
        client.Register<IPlayerClient>(inbox);

        var unbound = Assert.ThrowsAsync<RpcException>(async () => await client.Player(0).AddGold(1));
        Assert.That(unbound!.Status, Is.EqualTo(RpcStatus.Forbidden), "actor calls need a bound session");

        Assert.That(await client.Auth().Login(42), Is.True);
        Assert.That(await client.Player(999).AddGold(10), Is.EqualTo(10), "the key comes from the binding");
        Assert.That(await gateway.Player(42).AddGold(0), Is.EqualTo(10));
        await Eventually(() => inbox.Gold == 10, "push to the session was not delivered");
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
            await client.Auth().Login(100 + i);
            await client.Player(0).JoinRoom(7);
        }

        var outsider = await Connect(gateways[0]);
        var outsiderInbox = new Inbox();
        outsider.Register<IPlayerClient>(outsiderInbox);
        await outsider.Auth().Login(200);

        gateways[0].Player(100).Say(7, "hello");
        await Eventually(() => members.All(m => m.Chat.Contains("100:hello")), "broadcast missing");
        await Task.Delay(100); // a stray delivery would arrive about as fast as the ones above
        Assert.That(outsiderInbox.Chat, Is.Empty);
    }
}
