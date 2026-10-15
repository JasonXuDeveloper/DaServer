using DaServer.Cluster;
using NUnit.Framework;

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
            new HostedHandler { MessageId = Message, ImplementationId = Actor, Actor = true },
            new HostedHandler { MessageId = Message, ImplementationId = Service, Actor = false },
        ],
    };

    [Test]
    public void KeysStayWithASuspectMemberAndMoveWhenItIsDead()
    {
        var members = new[] { Member(1, MemberStatus.Up), Member(2, MemberStatus.Up), Member(3, MemberStatus.Up) };
        var view = new ClusterView(members, 1);
        long key = 1;
        while (view.ImplementationOwner(Actor, key)!.NodeId != 3) key++;

        members[2].Status = MemberStatus.Suspect; // a pause or a network blip
        var suspect = new ClusterView(members, 2);
        Assert.That(suspect.ImplementationOwner(Actor, key)!.NodeId, Is.EqualTo(3), "no second activation while only suspected");
        Assert.That(suspect.TryGetRoute(Message, actor: false, out var service, out _) && service.Nodes.Length == 2,
            "no new stateless work for a suspect");

        members[2].Status = MemberStatus.Dead;
        Assert.That(new ClusterView(members, 3).ImplementationOwner(Actor, key)!.NodeId, Is.Not.EqualTo(3), "moves once dead");
    }
}
