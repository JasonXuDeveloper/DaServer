using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;

namespace DaServer.Benchmark;

// Benchmark business code, written like user code: an actor with state in a component, a client-accessible service
// that manages a broadcast group, and a client callback.

public interface ICounter : IActor
{
    ValueTask<int> Add(int delta);
}

[ClientAccessible]
public interface IRoom : IService
{
    ValueTask Join(long room);
    ValueTask Broadcast(long room, int sequence);
}

public interface IRoomClient : IClientCallback
{
    void OnTick(int sequence);
}

public record struct CounterState(int Value) : IComponent;

public sealed class Counter : Actor<CounterState>, ICounter
{
    public ValueTask<int> Add(int delta) => new(State.Value += delta);
}

public sealed class Room : Service, IRoom
{
    public ValueTask Join(long room)
    {
        Cluster.JoinGroup(Caller, new ClientGroup(room));
        return default;
    }

    public ValueTask Broadcast(long room, int sequence)
    {
        Cluster.RoomClient(new ClientGroup(room)).OnTick(sequence);
        return default;
    }
}
