using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using EasyEcs;
using Nino.Core;

namespace DaServer.Benchmark;

// Benchmark business code, written like user code: an actor with state in a component that also handles an event, a
// public room actor whose groups clients join, and a message pushed to clients.

[NinoType, Message] public partial record struct Add(int Delta) : IRequest<int>;

/// <summary>Event published to a counter's key.</summary>
[NinoType, Message] public partial record struct Bumped(int By);

/// <summary>Join the room's group, or <see cref="Groups"/> groups of it (cells around a player in a map).</summary>
[NinoType, Message(Flow.FromClient, AnyKey = true)] public partial record struct Join(int Groups) : IRequest<bool>;

/// <summary>One message to each of <see cref="Groups"/> groups in the same turn.</summary>
[NinoType, Message] public partial record struct Broadcast(int Groups, int Sequence) : IRequest<bool>;

/// <summary>Pushed to clients.</summary>
[NinoType, Message(Flow.ToClient)] public partial record struct Tick(int Sequence);

public record struct CounterState(int Value) : IComponent;

public sealed class Counter : Actor<CounterState>
{
    [Handle] public int On(Add m) => State.Value += m.Delta;

    [Handle] public void On(Bumped e) => State.Value += e.By;
}

public sealed class Room : Actor
{
    [Handle]
    public bool On(Join m)
    {
        for (int i = 0; i < m.Groups; i++) Cluster.JoinGroup(CallerSession, GroupOf(i));
        return true;
    }

    [Handle]
    public bool On(Broadcast m)
    {
        for (int i = 0; i < m.Groups; i++) Cluster.Send(GroupOf(i), new Tick(m.Sequence));
        return true;
    }

    private static readonly uint Cells = RpcIds.Fnv1a("DaServer.Benchmark.Cells");

    private ClientGroup GroupOf(int index) => index == 0 ? Group : ClientGroup.Of(Cells, Key * 16 + index);
}

/// <summary>Client side: counts the ticks it receives.</summary>
internal sealed class Receiver(Countdown countdown)
{
    [Handle] public void On(Tick _) => countdown.Signal();
}
