using DaServer;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// What the servers push to one player, written into its world (run on the world's thread by <see cref="PushSystem"/>
/// through the client's callback queue). Pushes of a map the player left arrive late and are dropped.
/// </summary>
internal sealed class Pushes(Entity player)
{
    [Handle]
    public void On(Motion m)
    {
        Apply(m);
        m.Avatars.Return();
    }

    private void Apply(in Motion m)
    {
        var me = player.Get<Me>();
        if (m.Map != me.Map) return;
        var avatars = player.TryGet<View>(out var view) ? view.Avatars : null; // a client without a view sees only itself
        long now = Environment.TickCount64;
        foreach (var moved in m.Avatars.Items)
        {
            var at = new Position(moved.X, moved.Y);
            var velocity = new Velocity(moved.VelocityX, moved.VelocityY);
            if (moved.Player == me.Player)
            {
                player.Get<Position>() = at; // the map's word corrects the prediction
                player.Get<Velocity>() = velocity;
            }
            else if (avatars != null)
                avatars[moved.Player] = new Track(at, velocity, now);
        }
    }

    [Handle]
    public void On(Gone g)
    {
        if (g.Map == player.Get<Me>().Map && player.TryGet<View>(out var view)) view.Avatars.Remove(g.Player);
    }

    [Handle] public void On(RealmStatus status) => player.Get<Realm>() = new Realm(status.Tick, status.Online);

    [Handle] public void On(Chat chat) => player.Get<Heard>() = new Heard(chat.Player, chat.Text);
}
