using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;

namespace DaServer.Benchmark;

// Benchmark business code, written like user code: an actor with state in a component that also handles a keyed
// event, a public room actor whose group clients join, and a client callback.

public interface ICounter : IActor
{
    ValueTask<int> Add(int delta);
}

public interface IRoom : IPublicActor
{
    ValueTask Join();
    ValueTask Broadcast(int sequence);
}

[Nino.Core.NinoType]
public readonly partial record struct Bumped(long Key, int By) : IKeyedEvent;

public interface IRoomClient : IClientCallback
{
    void OnTick(int sequence);
}

public record struct CounterState(int Value) : IComponent;

public sealed class Counter : Actor<CounterState>, ICounter, IHandle<Bumped>
{
    public ValueTask<int> Add(int delta) => new(State.Value += delta);

    public ValueTask Handle(Bumped e)
    {
        State.Value += e.By;
        return default;
    }
}

public sealed class Room : Actor, IRoom
{
    public ValueTask Join()
    {
        Cluster.JoinGroup(Caller, Group);
        return default;
    }

    public ValueTask Broadcast(int sequence)
    {
        Cluster.RoomClient(Group).OnTick(sequence);
        return default;
    }
}
