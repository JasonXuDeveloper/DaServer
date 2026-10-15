using DaServer;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// What servers push to one player, turned into data (run on the world's thread by <see cref="PushSystem"/>, through
/// the client's callback queue). Structural changes (avatars coming into view, leaving) happen here; no ref to a
/// component is held across them.
/// </summary>
internal sealed class Pushes(World world, Entity player)
{
    [Handle]
    public void On(Motion m)
    {
        if (!InMap(m.Map)) return; // late, from the map the player left
        var me = player.Get<Me>().Player;
        var avatars = player.Get<View>().Avatars;
        foreach (var moved in m.Avatars)
        {
            var position = new Position(moved.X, moved.Y);
            var velocity = new Velocity(moved.VelocityX, moved.VelocityY);
            if (moved.Player == me)
            {
                player.Get<Position>() = position; // the server's word corrects the prediction
                player.Get<Velocity>() = velocity;
            }
            else if (avatars.TryGetValue(moved.Player, out var avatar))
            {
                avatar.Get<Position>() = position;
                avatar.Get<Velocity>() = velocity;
            }
            else
            {
                avatars[moved.Player] = world.CreateEntity(position, velocity, new Remote(player, moved.Player));
            }
        }

        player.Get<View>().Updates += m.Avatars.Count;
    }

    [Handle]
    public void On(Gone g)
    {
        if (InMap(g.Map) && player.Get<View>().Avatars.Remove(g.Player, out var avatar)) avatar.Destroy();
    }

    [Handle]
    public void On(RealmStatus status)
    {
        ref var realm = ref player.Get<Realm>();
        realm = realm with
        {
            Tick = status.Tick, Online = status.Online, ServerTimeTicks = status.ServerTimeTicks,
            FirstTick = realm.FirstTick == 0 ? status.Tick : realm.FirstTick, Received = realm.Received + 1,
        };
    }

    [Handle] public void On(Chat _) => player.Get<Heard>().Lines++;

    private bool InMap(MapId map) => player.TryGet<Me>(out var me) && me.Map == map;
}
