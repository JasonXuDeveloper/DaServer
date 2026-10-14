using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Game;

/// <summary>Persistent player state: the component on the actor's entity.</summary>
public record struct PlayerState(string Name, long MapId, int Logins, SessionRef Session) : IComponent;

/// <summary>The player: its own state, and which map it is in. The map simulates the avatar.</summary>
public sealed class Player : Actor<PlayerState>, IPlayer
{
    public override ValueTask OnActivateAsync()
    {
        // Load from storage here (await is fine: messages wait until activation completes).
        State.Name = "player-" + Key;
        return default;
    }

    [Handle]
    public ValueTask On(PlayerLoggedIn e)
    {
        State.Logins++;
        State.Session = e.Session;
        Cluster.Realm(IRealm.Id).PlayerOnline(Key);
        return default;
    }

    [Handle]
    public ValueTask On(SessionClosed e)
    {
        // The map got its own SessionClosed (the session was bound to it too) and removed the avatar.
        State.Session = default;
        State.MapId = 0;
        Cluster.Realm(IRealm.Id).PlayerOffline(Key);
        return default;
    }

    public ValueTask<PlayerInfo> GetInfo() => new(new PlayerInfo(Key, State.Name, State.MapId, State.Logins));

    public async ValueTask EnterMap(long mapId)
    {
        long previous = State.MapId;
        if (previous == mapId) return;
        if (previous != 0) await Cluster.Map(previous).Leave(Caller);
        await Cluster.Map(mapId).Enter(Key, Caller); // Caller holds for the whole turn
        State.MapId = mapId;             // State is re-read after awaiting (never hold the ref across await)
    }
}
