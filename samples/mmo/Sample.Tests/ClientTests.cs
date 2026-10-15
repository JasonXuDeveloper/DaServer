using DaServer.Client;
using EasyEcs;
using NUnit.Framework;
using Sample.Client;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Tests;

/// <summary>The client's logic: pushes become entities, prediction runs the shared rules, the view follows the grid.</summary>
public class ClientTests
{
    private World _world = null!;
    private ClusterClient _client = null!;
    private Entity _player;
    private Pushes _pushes = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new World();
        _world.AddSystem<MovementSystem>();
        _world.AddSystem<ViewSystem>();
        _client = new ClusterClient(); // not connected: these tests never send
        _player = Player.Create(_world, _client);
        _player.Add<Me>() = new Me(new PlayerId(1), new MapId(1));
        _player.Get<Position>() = new Position(150, 150); // cell (1, 1)
        _pushes = new Pushes(_world, _player);
    }

    [TearDown]
    public async Task TearDown()
    {
        _world.Dispose();
        await _client.DisposeAsync();
    }

    private static Motion Motion(params Moved[] moved) => new([.. moved]);

    [Test]
    public void AvatarsInViewAreEntitiesUntilTheyAreGone()
    {
        _pushes.On(Motion(new Moved(new PlayerId(2), 160, 160, 1, 0)));
        Assert.That(_player.Get<View>().Avatars, Contains.Key(new PlayerId(2)));
        var avatar = _player.Get<View>().Avatars[new PlayerId(2)];

        _world.Update(); // extrapolated between updates
        Assert.That(avatar.Get<Position>(), Is.EqualTo(new Position(161, 160)));

        _pushes.On(new Gone(new PlayerId(2)));
        Assert.That(_player.Get<View>().Avatars, Is.Empty);
        Assert.That(avatar.IsAlive, Is.False);
    }

    [Test]
    public void TheServerCorrectsThePrediction()
    {
        _pushes.On(Motion(new Moved(new PlayerId(1), 300, 200, 0, 2)));
        Assert.That(_player.Get<Position>(), Is.EqualTo(new Position(300, 200)));
        Assert.That(_player.Get<Velocity>(), Is.EqualTo(new Velocity(0, 2)));
        Assert.That(_player.Get<View>().Avatars, Is.Empty, "the own avatar is the player, not a remote one");
    }

    [Test]
    public void AvatarsOutsideTheThreeByThreeLeaveTheView()
    {
        _pushes.On(Motion(new Moved(new PlayerId(3), 250, 150, 0, 0), new Moved(new PlayerId(4), 450, 150, 0, 0)));
        _world.Update();
        Assert.That(_player.Get<View>().Avatars.Keys, Is.EquivalentTo(new[] { new PlayerId(3) }), "cell (4, 1) is not near (1, 1)");
    }

    [Test]
    public void RealmStatusesAreCounted()
    {
        _pushes.On(new RealmStatus(10, 5, 0));
        _pushes.On(new RealmStatus(15, 6, 0));
        var realm = _player.Get<Realm>();
        Assert.That((realm.FirstTick, realm.Tick, realm.Online, realm.Received), Is.EqualTo((10L, 15L, 6, 2L)));
    }
}
