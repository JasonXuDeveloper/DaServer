using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

/// <summary>Who is online and the realm's tick counter.</summary>
public record struct RealmState(HashSet<long> Online, long Tick) : IComponent;

/// <summary>The realm: players report in and out; <see cref="RealmBroadcastSystem"/> tells every client about it.</summary>
public sealed class Realm : Actor<RealmState>, IRealm
{
    public override ValueTask OnActivateAsync()
    {
        State.Online = new HashSet<long>();
        return default;
    }

    public void PlayerOnline(long playerId) => State.Online.Add(playerId);

    public void PlayerOffline(long playerId) => State.Online.Remove(playerId);
}

/// <summary>Every <see cref="EveryTicks"/> ticks, the realm status goes to every session of the cluster.</summary>
public sealed partial class RealmBroadcastSystem : EcsSystem
{
    public const int EveryTicks = 5;

    [ForEach]
    private static void Broadcast(ref RealmState realm, World world)
    {
        if (++realm.Tick % EveryTicks != 0) return;
        world.Cluster().PlayerClient(ClientGroup.All)
            .OnRealm(new RealmStatus(realm.Tick, realm.Online.Count, DateTime.UtcNow.Ticks));
    }
}
