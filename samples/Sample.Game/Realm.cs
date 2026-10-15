using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

/// <summary>Who is online and the realm's tick counter.</summary>
public struct RealmState() : IComponent
{
    public readonly HashSet<long> Online = new();
    public long Tick;
}

/// <summary>The realm: players report in and out; <see cref="RealmBroadcastSystem"/> tells every client about it.</summary>
public sealed class Realm : Actor<RealmState>
{
    [Handle] public void On(PlayerOnline m) => State.Online.Add(m.PlayerId);

    [Handle] public void On(PlayerOffline m) => State.Online.Remove(m.PlayerId);
}

/// <summary>Every <see cref="EveryTicks"/> ticks, the realm status goes to every session of the cluster.</summary>
public sealed partial class RealmBroadcastSystem : EcsSystem
{
    public const int EveryTicks = 5;

    [ForEach]
    private static void Broadcast(ref RealmState realm, World world)
    {
        if (++realm.Tick % EveryTicks != 0) return;
        world.Cluster().Send(ClientGroup.All, new RealmStatus(realm.Tick, realm.Online.Count, DateTime.UtcNow.Ticks));
    }
}
