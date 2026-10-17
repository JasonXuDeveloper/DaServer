namespace DaServer.Tests;

/// <summary>
/// Two builds side by side (V2/V3): node b runs an "older" build that does not announce a message. Services receive
/// only what their build handles; an actor stays where its key is owned, and a message its owner's build lacks is
/// refused (the caller retries once the owner is upgraded) instead of reaching a node that cannot run it.
/// </summary>
public class RollingUpgradeTests : ClusterTest
{
    private static Func<uint, bool> Lacks<TMessage>() where TMessage : struct => id => id == TypeId<TMessage>.Value;

    [Test]
    public async Task AServiceMessageGoesOnlyToNodesWhoseBuildHandlesIt()
    {
        var a = await Start("a");
        var b = await Start("b", configure: o => o.Unannounced = Lacks<WhichNode>());
        await Converged();

        // b hosts the echo service too, but its build has no WhichNode: even b's own calls go to a.
        for (int i = 0; i < 10; i++) Assert.That(await b.Ask(new WhichNode()), Is.EqualTo(a.Id));
        Assert.That(await b.Ask(new EchoText("x")), Is.EqualTo("x"), "messages both builds handle stay local");
    }

    [Test]
    public async Task AnActorMessageItsOwnersBuildLacksIsRefusedUntilTheOwnerHasIt()
    {
        var a = await Start("a");
        var b = await Start("b", configure: o => o.Unannounced = Lacks<Reset>());
        await Converged();
        long key = KeyOwnedBy<CounterId>(b);

        Assert.That(await a.Ask(new CounterId(key), new Add(2)), Is.EqualTo(2), "the owner runs what its build has");
        var error = Assert.ThrowsAsync<RpcException>(async () => await a.Ask(new CounterId(key), new Reset()));
        Assert.That(error!.Status, Is.EqualTo(RpcStatus.NotHosted));
        Assert.That(await a.Ask(new CounterId(key), new Get()), Is.EqualTo(2), "one activation: the key never moved to a");

        long onA = KeyOwnedBy<CounterId>(a);
        Assert.That(await b.Ask(new CounterId(onA), new Reset()), Is.True, "an owner whose build has it runs it");
    }

    [Test]
    public async Task TheViewShowsWhatEachMemberRuns()
    {
        var a = await Start("a");
        var b = await Start("b");
        await Converged();
        Assert.That(a.View.TryGetMember(b.Id, out var member) ? member.Build : null, Is.EqualTo(Runtime.Node.Build));
        Assert.That(member!.Build, Does.StartWith("daserver ").And.Contain("; app "));
    }
}

/// <summary>The view's routing while builds differ, on hand-made cards.</summary>
public class MixedBuildViewTests
{
    private const uint Message = 7, Other = 8, Actor = 100, Service = 200;

    private static ClusterMember Member(ulong id, params HostedMessage[] handlers) =>
        new() { NodeId = id, Name = "n" + id, Host = "127.0.0.1", Port = (int)id, Status = MemberStatus.Up, Handlers = handlers };

    private static HostedMessage Handles(uint handlerType, bool actor, uint message = Message) =>
        new() { MessageId = message, HandlerTypeId = handlerType, Actor = actor };

    [Test]
    public void AnOwnerLackingTheMessageForAnotherHandlerTypeStillGetsItForItsOwn()
    {
        // Both builds handle the message on the actor type and host the service; only the newer service handles it.
        var older = Member(1, Handles(Actor, true), Handles(Service, false, Other));
        var newer = Member(2, Handles(Actor, true), Handles(Service, false));
        var view = new ClusterView([older, newer]);

        Assert.That(view.TryGetHandlerTypeRoute(Actor, out var actorRoute));
        for (long key = 0; key < 64; key++)
            Assert.That(view.Target(in actorRoute, Message, key, 0), Is.SameAs(view.HandlerTypeOwner(Actor, key)), "the actor's owner, old or new");

        Assert.That(view.TryGetRoute(Message, actor: false, out var serviceRoute, out _));
        Assert.That(serviceRoute.Nodes, Is.EqualTo(new[] { newer }), "the service only where the build has it");
    }
}
