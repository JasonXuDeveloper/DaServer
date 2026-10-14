using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;

namespace DaServer.Benchmark;

// Benchmark business code, written like user code: an actor with state in a component that also handles a keyed
// event, a public room actor whose group clients join, and a client callback.

[Actor]
public interface ICounter
{
    ValueTask<int> Add(int delta);
}

[Actor, Client(AnyKey = true)]
public interface IRoom
{
    ValueTask Join();
    ValueTask Broadcast(int sequence);
}

[Nino.Core.NinoType]
public readonly partial record struct Bumped([property: Key] long Key, int By);

[Push]
public interface IRoomClient
{
    void OnTick(int sequence);
}

public record struct CounterState(int Value) : IComponent;

public sealed class Counter : Actor<CounterState>, ICounter
{
    public ValueTask<int> Add(int delta) => new(State.Value += delta);

    [Handle]
    public ValueTask On(Bumped e)
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
