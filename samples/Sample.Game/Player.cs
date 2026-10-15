using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

/// <summary>Persistent player state: the component on the actor's entity.</summary>
public record struct PlayerState(string Name, long MapId, int Logins, SessionRef Session) : IComponent;

/// <summary>The player: its own state, and which map it is in. The map simulates the avatar.</summary>
public sealed class Player : Actor<PlayerState>
{
    public override ValueTask OnLoad()
    {
        // Load from storage here (await is fine: messages wait until loading completes).
        State.Name = "player-" + Key;
        return default;
    }

    [Handle]
    public async ValueTask<bool> On(Attach m)
    {
        await BindSession(m.Session); // the client's GetInfo/EnterMap reach this player from now on
        State.Logins++;               // State is re-read after awaiting (never hold the ref across await)
        State.Session = m.Session;
        Cluster.Send(Keys.Realm, new PlayerOnline(Key));
        return true;
    }

    [Handle]
    public void On(SessionClosed _)
    {
        // The map got its own SessionClosed (the session was bound to it too) and removed the avatar.
        State.Session = default;
        State.MapId = 0;
        Cluster.Send(Keys.Realm, new PlayerOffline(Key));
    }

    [Handle]
    public void On(SessionResync e) => State.Session = e.Session; // reconnected: not offline, nothing else to resend

    [Handle]
    public PlayerInfo On(GetInfo _) => new(Key, State.Name, State.MapId, State.Logins);

    [Handle]
    public async ValueTask<bool> On(EnterMap m)
    {
        long previous = State.MapId;
        if (previous == m.MapId) return true;
        if (previous != 0) await Cluster.Ask(previous, new Leave(CallerSession));
        await Cluster.Ask(m.MapId, new Enter(Key, CallerSession)); // CallerSession holds for the whole turn
        State.MapId = m.MapId;
        return true;
    }
}
