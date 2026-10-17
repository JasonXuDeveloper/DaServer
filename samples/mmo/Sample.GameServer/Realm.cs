using DaServer;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.GameServer;

/// <summary>The realm: players report in and out; <see cref="RealmBroadcastSystem"/> tells every client about it.</summary>
public sealed class Realm : Actor<RealmId, RealmState>
{
    [Handle] public void On(PlayerOnline m) => State.Online.Add(m.Player);

    [Handle] public void On(PlayerOffline m) => State.Online.Remove(m.Player);
}

/// <summary>Every <see cref="Timing.RealmStatusEveryTicks"/> ticks, the realm status goes to every session of the cluster.</summary>
public sealed partial class RealmBroadcastSystem : EcsSystem
{
    public override int UpdateInterval => Timing.RealmStatusEveryTicks;

    [ForEach]
    private static void Broadcast(ref RealmState realm, World world)
    {
        realm.Ticks += Timing.RealmStatusEveryTicks;
        world.Cluster().Send(ClientGroup.All, new RealmStatus(realm.Ticks, realm.Online.Count, world.Time().GetUtcNow().UtcTicks));
    }
}
