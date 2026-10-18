using DaServer;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.GameServer;

/// <summary>
/// The player: its profile (stored, restored before <see cref="OnLoad"/>), and which map it is in. The map simulates
/// the avatar.
/// </summary>
public sealed class Player : Actor<PlayerId, PlayerState>
{
    public override Reply OnLoad()
    {
        if (!Entity.Has<PlayerProfile>()) Entity.Add<PlayerProfile>() = new PlayerProfile("player-" + Id.Value, 0); // a new player
        return default;
    }

    [Handle]
    public async Reply<bool> On(Attach m)
    {
        await BindSession(m.Session); // the client's GetInfo/EnterMap reach this player from now on
        Entity.Get<PlayerProfile>().Logins++; // components are re-read after awaiting (never hold a ref across await)
        State.Session = m.Session;
        Cluster.Send(RealmId.The, new PlayerOnline(Id));
        return true;
    }

    [Handle]
    public void On(SessionClosed _)
    {
        // The map got its own SessionClosed (the session was bound to it too) and removed the avatar.
        State.Session = default;
        State.Map = default;
        Cluster.Send(RealmId.The, new PlayerOffline(Id));
    }

    [Handle]
    public void On(SessionResumed e) => State.Session = e.Session; // resumed: not offline, nothing else to resend

    [Handle]
    public PlayerInfo On(GetInfo _)
    {
        ref var profile = ref Entity.Get<PlayerProfile>();
        return new PlayerInfo(Id, profile.Name, State.Map, profile.Logins);
    }

    [Handle]
    public async Reply<bool> On(EnterMap m)
    {
        var previous = State.Map;
        if (previous == m.Map) return true;
        if (previous != default) await Cluster.Ask(previous, new Leave(Caller));
        await Cluster.Ask(m.Map, new Enter(Id, Caller)); // Caller holds for the whole turn
        State.Map = m.Map;
        return true;
    }
}
