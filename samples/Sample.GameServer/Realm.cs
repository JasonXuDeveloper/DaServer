using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.GameServer;

/// <summary>Who is online.</summary>
public struct RealmState() : IComponent
{
    public readonly HashSet<long> Online = new();
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

    public override int UpdateInterval => EveryTicks;

    [ForEach]
    private static void Broadcast(in RealmState realm, World world) =>
        world.Cluster().Send(ClientGroup.All, new RealmStatus(world.Tick(), realm.Online.Count, DateTime.UtcNow.Ticks));
}
