namespace DaServer.Tests;

/// <summary>Rendezvous placement over the membership view.</summary>
public class PlacementTests
{
    private const uint Message = 7, Actor = 9, Service = 10;

    private static ClusterMember Member(ulong id, MemberStatus status) => new()
    {
        NodeId = id, Name = "n" + id, Status = status,
        Handlers =
        [
            new HostedMessage { MessageId = Message, HandlerTypeId = Actor, Actor = true },
            new HostedMessage { MessageId = Message, HandlerTypeId = Service, Actor = false },
        ],
    };

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
}
