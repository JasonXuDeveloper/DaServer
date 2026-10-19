namespace DaServer.Tests;

/// <summary>Rendezvous placement over the membership view.</summary>
public class PlacementTests
{
    private const uint Message = 7, Actor = 9, Service = 10;

    private static ClusterMember Member(ulong id, MemberStatus status) =>
        Views.Member(id, status, Views.Handles(Actor, true, Message), Views.Handles(Service, false, Message));

    [Test]
    public void KeysStayWithASuspectMemberAndMoveWhenItIsDead()
    {
        var members = new[] { Member(1, MemberStatus.Up), Member(2, MemberStatus.Up), Member(3, MemberStatus.Up) };
        var view = new ClusterView(members);
        long key = 1;
        while (view.HandlerTypeOwner(Actor, key)!.NodeId != 3) key++;

        members[2].Status = MemberStatus.Suspect; // a pause or a network blip
        var suspect = new ClusterView(members);
        Assert.That(suspect.HandlerTypeOwner(Actor, key)!.NodeId, Is.EqualTo(3), "no second loaded actor while only suspected");
        Assert.That(suspect.TryGetRoute(Message, actor: false, out var service, out _) && service.Nodes.Length == 2,
            "no new stateless work for a suspect");

        members[2].Status = MemberStatus.Dead;
        Assert.That(new ClusterView(members).HandlerTypeOwner(Actor, key)!.NodeId, Is.Not.EqualTo(3), "moves once dead");
    }

    [Test]
    public void ANodeJoiningTakesOnlyTheSlotsItWinsAndTheyAreEven()
    {
        var three = new[] { Member(1, MemberStatus.Up), Member(2, MemberStatus.Up), Member(3, MemberStatus.Up) };
        var before = new ClusterView(three);
        var after = new ClusterView([.. three, Member(4, MemberStatus.Up)]);
        int moved = 0;
        var perNode = new Dictionary<ulong, int>();
        for (long key = 0; key < 40_000; key++)
        {
            ulong was = before.HandlerTypeOwner(Actor, key)!.NodeId, now = after.HandlerTypeOwner(Actor, key)!.NodeId;
            if (was != now)
            {
                moved++;
                Assert.That(now, Is.EqualTo(4UL), "a key only moves to the node that joined");
                Assert.That(after.HandlerTypeOwnerWithout(Actor, key, 4)!.NodeId, Is.EqualTo(was), "and came from its owner without it");
            }

            perNode[now] = perNode.GetValueOrDefault(now) + 1;
        }

        Assert.That(moved, Is.InRange(8_000, 12_000), "about a quarter of the keys moved");
        foreach (var (_, count) in perNode) Assert.That(count, Is.InRange(8_000, 12_000), "each node owns about a quarter");
    }

    [Test]
    public void ServiceCallsGoToTheLessLoadedOfTwoNodes()
    {
        var members = new[] { Member(1, MemberStatus.Up), Member(2, MemberStatus.Up), Member(3, MemberStatus.Up) };
        var view = new ClusterView(members);
        Assert.That(view.TryGetRoute(Message, actor: false, out var route, out _));
        var counts = new Dictionary<ulong, int>();
        void Calls(int n)
        {
            counts.Clear();
            for (int i = 0; i < n; i++)
            {
                ulong node = view.Target(in route, Message, 0, localNodeId: 99)!.NodeId; // a caller not hosting the service
                counts[node] = counts.GetValueOrDefault(node) + 1;
            }
        }

        Calls(3_000);
        foreach (var (_, count) in counts) Assert.That(count, Is.InRange(800, 1_200), "equal loads: an even spread");

        members[1].Load = 500; // node 2's shards have a backlog (gossiped)
        Calls(3_000);
        Assert.That(counts.GetValueOrDefault(2UL), Is.Zero, "the busy node is never the less loaded of two");
        Assert.That(counts[1] + counts[3], Is.EqualTo(3_000));

        Assert.That(view.Target(in route, Message, 0, localNodeId: 2)!.NodeId, Is.EqualTo(2UL), "a node hosting the service still serves itself");
    }
}
