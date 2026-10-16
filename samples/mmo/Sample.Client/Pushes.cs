using DaServer;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// What the servers push to one player, written into its world (run on the world's thread by <see cref="PushSystem"/>
/// through the client's callback queue). Pushes of a map the player left arrive late and are dropped.
/// </summary>
internal sealed class Pushes(World world, Entity player)
{
    [Handle]
    public void On(Motion m)
    {
        var me = player.Get<Me>();
        if (m.Map != me.Map) return;
        var avatars = player.Get<View>().Avatars;
        long now = Environment.TickCount64;
        foreach (var moved in m.Avatars)
        {
            var at = new Position(moved.X, moved.Y);
            var velocity = new Velocity(moved.VelocityX, moved.VelocityY);
            if (moved.Player == me.Player)
            {
                player.Get<Position>() = at; // the map's word corrects the prediction
                player.Get<Velocity>() = velocity;
            }
            else if (avatars.TryGetValue(moved.Player, out var avatar))
                avatar.Get<Track>() = new Track(at, velocity, now);
            else
                avatars[moved.Player] = world.CreateEntity(new Track(at, velocity, now));
        }
    }

    [Handle]
    public void On(Gone g)
    {
        if (g.Map == player.Get<Me>().Map && player.Get<View>().Avatars.Remove(g.Player, out var avatar)) avatar.Destroy();
    }

    [Handle] public void On(RealmStatus status) => player.Get<Realm>() = new Realm(status.Tick, status.Online);

    [Handle] public void On(Chat chat) => player.Get<Heard>() = new Heard(chat.Player, chat.Text);
}
