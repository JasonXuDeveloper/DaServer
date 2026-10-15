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

[Actor, FromClient(AnyKey = true)]
public interface IRoom
{
    ValueTask Join(int groups);
    ValueTask Broadcast(int groups, int sequence);
}

[Nino.Core.NinoType]
public readonly partial record struct Bumped([property: ActorKey] long Key, int By);

[ToClient]
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
    /// <summary>Join the room's group, or <paramref name="groups"/> groups of it (cells around a player in a map).</summary>
    public ValueTask Join(int groups)
    {
        for (int i = 0; i < groups; i++) Cluster.JoinGroup(CallerSession, GroupOf(i));
        return default;
    }

    /// <summary>One message to each of <paramref name="groups"/> groups in the same turn.</summary>
    public ValueTask Broadcast(int groups, int sequence)
    {
        for (int i = 0; i < groups; i++) Cluster.RoomClient(GroupOf(i)).OnTick(sequence);
        return default;
    }

    private static readonly uint Cells = RpcIds.Fnv1a("DaServer.Benchmark.Cells");

    private ClientGroup GroupOf(int index) => index == 0 ? Group : ClientGroup.Of(Cells, Key * 16 + index);
}
