using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Protocol;
using DaServer.Rpc;
using NUnit.Framework;

namespace DaServer.Tests;

/// <summary>Client sessions through a gateway: binding, access control, push and group broadcast.</summary>
public class GatewayTests
{
    private sealed class ClientInbox : IPlayerClient
    {
        public readonly ConcurrentQueue<string> Chat = new();
        public volatile int Gold;

        public void OnChat(long from, string text) => Chat.Enqueue($"{from}:{text}");

        public void OnGold(int gold) => Gold = gold;
    }

    private TestCluster _cluster = null!;

    [SetUp]
    public void Create() => _cluster = new TestCluster();

    [TearDown]
    public async Task Dispose() => await _cluster.DisposeAsync();

    [Test]
    public async Task LoginBindsTheSessionAndCallsReachTheBoundActor()
    {
        var gateway = await _cluster.StartNode(TestCluster.Options("gw", clientPort: 0));
        await _cluster.Join("game");
        await _cluster.Converged(2);

        await using var client = await _cluster.Connect(gateway);
        var inbox = new ClientInbox();
        client.Register<IPlayerClient>(inbox);

        var unbound = Assert.ThrowsAsync<RpcException>(async () => await client.Player(0).AddGold(1));
        Assert.That(unbound!.Status, Is.EqualTo(RpcStatus.Forbidden), "actor calls need a bound session");

        Assert.That(await client.Auth().Login(42), Is.True);
        Assert.That(await client.Player(999).AddGold(10), Is.EqualTo(10), "the key comes from the binding, not the client");
        Assert.That(await gateway.Player(42).AddGold(0), Is.EqualTo(10));

        await TestCluster.Eventually(() => inbox.Gold == 10, "push to the session was not delivered");
    }

    [Test]
    public async Task ClientsCannotCallNonClientAccessibleContracts()
    {
        var gateway = await _cluster.StartNode(TestCluster.Options("gw", clientPort: 0));
        await using var client = await _cluster.Connect(gateway);
        var error = Assert.ThrowsAsync<RpcException>(async () => await client.Counter(1).Get());
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.Forbidden));
    }

    [Test]
    public async Task GroupBroadcastReachesEveryMemberOnEveryGateway()
    {
        var gw1 = await _cluster.StartNode(TestCluster.Options("gw1", clientPort: 0));
        var gw2 = await _cluster.Join("gw2", clientPort: 0);
        await _cluster.Join("game");
        await _cluster.Converged(3);

        var inboxes = new ClientInbox[4];
        for (int i = 0; i < inboxes.Length; i++)
        {
            var client = await _cluster.Connect(i % 2 == 0 ? gw1 : gw2);
            client.Register<IPlayerClient>(inboxes[i] = new ClientInbox());
            Assert.That(await client.Auth().Login(100 + i), Is.True);
            await client.Player(0).JoinRoom(7);
        }

        // A non-member does not receive the broadcast.
        var outsider = await _cluster.Connect(gw1);
        var outsiderInbox = new ClientInbox();
        outsider.Register<IPlayerClient>(outsiderInbox);
        Assert.That(await outsider.Auth().Login(200), Is.True);

        gw1.Player(100).Say(7, "hello");
        foreach (var inbox in inboxes)
            await TestCluster.Eventually(() => inbox.Chat.TryPeek(out var m) && m == "100:hello", "broadcast missing");
        await Task.Delay(100);
        Assert.That(outsiderInbox.Chat, Is.Empty);
    }
}
