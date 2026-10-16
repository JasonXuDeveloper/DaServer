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
        ClientSystems.AddTo(_world);
        _client = new ClusterClient(); // not connected: these tests never send
        _player = Player.Create(_world, _client);
        _player.Get<Me>() = new Me(new PlayerId(1), new MapId(1));
        _player.Get<Position>() = new Position(150, 150); // cell (1, 1)
        _pushes = new Pushes(_world, _player);
    }

    [TearDown]
    public async Task TearDown()
    {
        _world.Dispose();
        await _client.DisposeAsync();
    }

    private static Motion Motion(params Moved[] moved) => new(new MapId(1), new Moves { Items = [.. moved] });

    private Dictionary<PlayerId, Entity> Avatars => _player.Get<View>().Avatars;

    [Test]
    public void AvatarsInViewAreTrackedUntilTheyAreGone()
    {
        _pushes.On(Motion(new Moved(new PlayerId(2), 160, 160, 1, 0)));
        var track = Avatars[new PlayerId(2)].Get<Track>();
        Assert.That(track.Now(track.AtMs + 1000), Is.EqualTo(new Position(160 + Timing.TickRate, 160)), "extrapolated per tick");
        Assert.That(track.Now(track.AtMs + 1_000_000).X, Is.EqualTo(MapGeometry.Size), "the border stops it");

        var avatar = Avatars[new PlayerId(2)];
        _pushes.On(new Gone(new MapId(1), new PlayerId(2)));
        Assert.That(Avatars, Is.Empty);
        Assert.That(avatar.IsAlive, Is.False);
    }

    [Test]
    public void TheMapCorrectsThePredictionAndLatePushesOfAnotherMapAreDropped()
    {
        _pushes.On(Motion(new Moved(new PlayerId(1), 300, 200, 0, 2)));
        Assert.That(_player.Get<Position>(), Is.EqualTo(new Position(300, 200)));
        Assert.That(_player.Get<Velocity>(), Is.EqualTo(new Velocity(0, 2)));
        Assert.That(Avatars, Is.Empty, "the own avatar is the player, not a tracked one");

        _pushes.On(new Motion(new MapId(9), new Moves { Items = [new Moved(new PlayerId(5), 150, 150, 0, 0)] }));
        Assert.That(Avatars, Is.Empty, "a push of a map the player is not in");
    }

    [Test]
    public void AvatarsOutsideTheThreeByThreeLeaveTheView()
    {
        _pushes.On(Motion(new Moved(new PlayerId(3), 250, 150, 0, 0), new Moved(new PlayerId(4), 450, 150, 0, 0)));
        for (int i = 0; i < Timing.TickRate; i++) _world.Update(); // the view is kept twice a second
        Assert.That(Avatars.Keys, Is.EquivalentTo(new[] { new PlayerId(3) }), "cell (4, 1) is not near (1, 1)");
    }

    [Test]
    public void SteeringMovesTheOwnAvatarAtOnce()
    {
        _player.Get<Steering>() = new Steering(9, 0);
        _world.Update();
        Assert.That(_player.Get<Velocity>(), Is.EqualTo(new Velocity(MapGeometry.MaxSpeed, 0)), "clamped like the map does");
        Assert.That(_player.Get<Position>(), Is.EqualTo(new Position(150 + MapGeometry.MaxSpeed, 150)), "predicted on the same tick");
    }

    [Test]
    public void TheRealmAndChatAreTheLatestHeard()
    {
        _pushes.On(new RealmStatus(10, 5, 0));
        _pushes.On(new RealmStatus(15, 6, 0));
        _pushes.On(new Chat(new PlayerId(7), "hi"));
        Assert.That(_player.Get<Realm>(), Is.EqualTo(new Realm(15, 6)));
        Assert.That(_player.Get<Heard>(), Is.EqualTo(new Heard(new PlayerId(7), "hi")));
    }
}
