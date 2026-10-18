using System.Collections.Concurrent;
using DaServer;
using DaServer.Testing;
using NUnit.Framework;
using Sample.Contracts;

namespace Sample.Tests;

/// <summary>
/// The sample's servers in one in-process cluster (DaServer.Testing) on a manual clock: the test decides when ticks
/// happen, so what the systems send per tick is counted exactly.
/// </summary>
public class ServerTests
{
    [Test]
    public void EveryMessageHasItsHandler() =>
        RouteCheck.Check(typeof(Sample.LoginServer.Account).Assembly, typeof(Sample.GameServer.Player).Assembly, typeof(Sample.MapServer.Map).Assembly);

    [Test]
    public async Task TheRealmReportsOnItsTicksAndTheMapMovesAvatarsOnly()
    {
        await using var cluster = await TestCluster.StartAsync(o =>
        {
            o.ManualTime = true;
            o.ClientConnections = true;
            o.TickRate = Timing.TickRate;
        });
        var heard = new Overheard();
        var client = await cluster.ConnectAsync(heard);
        var login = await client.Ask(new Login("ann"));
        Assert.That(login.Ok, Is.True);
        Assert.That(await client.Ask(new EnterMap(new MapId(1))), Is.True);

        await cluster.TickAsync(Timing.RealmStatusEveryTicks * 3);
        await cluster.WaitUntilAsync(() => heard.Status.Count >= 3, TimeSpan.FromSeconds(5));
        var status = heard.Status.ToArray();
        Assert.That(status, Has.Length.EqualTo(3), "one status every RealmStatusEveryTicks ticks, none in between");
        Assert.That(status[1].Tick - status[0].Tick, Is.EqualTo(Timing.RealmStatusEveryTicks));
        Assert.That(status[0].Online, Is.EqualTo(1));
        Assert.That(new DateTime(status[2].ServerTimeTicks).Year, Is.EqualTo(2000), "the realm reads the shards' clock");

        client.Send(new Move(1, 0));
        await Task.Delay(100); // the map took the input meanwhile: nothing moves until the clock does
        Assert.That(heard.Moving(login.Player), Is.False);
        for (int i = 0; i < 20 && !heard.Moving(login.Player); i++)
        {
            await cluster.TickAsync();
            await Task.Delay(10); // the push travels through the gateway
        }

        Assert.That(heard.Moving(login.Player), Is.True, "the map integrates input on its ticks and tells the viewers");
    }
}

/// <summary>What the test client heard (pushes arrive on the client's IO thread).</summary>
internal sealed class Overheard
{
    public readonly ConcurrentQueue<RealmStatus> Status = new();
    private readonly ConcurrentDictionary<PlayerId, float> _velocityX = new();

    public bool Moving(PlayerId player) => _velocityX.TryGetValue(player, out var vx) && vx > 0;

    [Handle] public void On(RealmStatus m) => Status.Enqueue(m);

    [Handle]
    public void On(Motion m)
    {
        foreach (var moved in m.Avatars.Items) _velocityX[moved.Player] = moved.VelocityX;
        m.Avatars.ReturnToPool();
    }
}
