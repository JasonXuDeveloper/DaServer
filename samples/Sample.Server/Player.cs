using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.Server;

/// <summary>Persistent player state: the component on the actor's entity.</summary>
public record struct PlayerState(string Name, long MapId, int Gold) : IComponent;

[Role("game")]
public sealed class Player : Actor<PlayerState>, IPlayer
{
    private const int MaxChatLength = 256;

    public override ValueTask OnActivateAsync()
    {
        // Load from storage here (await is fine: messages wait until activation completes).
        State.Name = "player-" + Key;
        return default;
    }

    public ValueTask<PlayerInfo> GetInfo() => new(new PlayerInfo(Key, State.Name, State.MapId, State.Gold));

    public async ValueTask EnterMap(long mapId)
    {
        var session = Caller;            // valid until the first await
        long previous = State.MapId;
        if (previous == mapId) return;
        if (previous != 0) await Cluster.Map(previous).Leave(Key);
        await Cluster.Map(mapId).Enter(Key, session);
        State.MapId = mapId;             // State is re-read after awaiting (never hold the ref across await)
    }

    public void Move(float dx, float dy)
    {
        if (State.MapId != 0) Cluster.Map(State.MapId).SetVelocity(Key, dx, dy);
    }

    public void Chat(string text)
    {
        if (State.MapId != 0 && text.Length <= MaxChatLength)
            Cluster.GameClient(new ClientGroup(State.MapId)).OnChat(Key, text);
    }
}
