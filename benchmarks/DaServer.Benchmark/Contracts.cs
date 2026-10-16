using System.Runtime.CompilerServices;
using EasyEcs;
using Nino.Core;

namespace DaServer.Benchmark;

// Benchmark business code, written like user code: an actor with state in a component that also takes one-way messages, a
// room actor whose groups clients join through a lobby service, and a message pushed to clients.

public readonly record struct CounterId(long Value) : IActorId;
public readonly record struct RoomId(long Value) : IActorId;

[NinoType, Message] public partial record struct Add(int Delta) : IRequest<int>;

/// <summary>One-way message to a counter.</summary>
[NinoType, Message] public partial record struct Bumped(int By);

/// <summary>Join the room's group, or <see cref="Groups"/> groups of it (cells around a player in a map).</summary>
[NinoType, Message, FromClient] public partial record struct Join(RoomId Room, int Groups) : IRequest<bool>;

/// <summary>The lobby admits a session to the room's groups.</summary>
[NinoType, Message] public partial record struct Admit(ClientSession Session, int Groups) : IRequest<bool>;

/// <summary>One message to each of <see cref="Groups"/> groups in the same turn.</summary>
[NinoType, Message] public partial record struct Broadcast(int Groups, int Sequence) : IRequest<bool>;

/// <summary>Pushed to clients.</summary>
[NinoType, Message, ToClient] public partial record struct Tick(int Sequence);


public sealed class Counter : Actor<CounterId, CounterState>
{
    [Handle] public int On(Add m) => State.Value += m.Delta;

    [Handle] public void On(Bumped e) => State.Value += e.By;
}

public sealed class Lobby : Service
{
    [Handle] public Reply<bool> On(Join m) => Cluster.Ask(m.Room, new Admit(Caller, m.Groups));
}

public sealed class Room : Actor<RoomId>
{
    [Handle]
    public bool On(Admit m)
    {
        for (int i = 0; i < m.Groups; i++) Cluster.Send(m.Session, new JoinGroup(GroupOf(i)));
        return true;
    }

    [Handle]
    public bool On(Broadcast m)
    {
        for (int i = 0; i < m.Groups; i++) Cluster.Send(GroupOf(i), new Tick(m.Sequence));
        return true;
    }


    private ClientGroup GroupOf(int index) => index == 0 ? Group : ClientGroup.Of<Cells>(Id.Value * 16 + index);
}

/// <summary>Client side: counts the ticks it receives.</summary>
internal sealed class Receiver(Countdown countdown)
{
    [Handle] public void On(Tick _) => countdown.Signal();
}

/// <summary>Group scope of the benchmark's extra broadcast groups.</summary>
public struct Cells;

// ---------------------------------------------------------------- a hot actor waiting for acknowledgements

public readonly record struct GateId(long Value) : IActorId;

/// <summary>Enter through the gate, which waits for the ledger's acknowledgement before answering (awaiting it).</summary>
[NinoType, Message] public partial record struct EnterAwaited(CounterId Ledger) : IRequest<bool>;

/// <summary>The same, replied when the acknowledgement arrives while the gate takes its next caller (ReplyWhen).</summary>
[NinoType, Message] public partial record struct EnterDeferred(CounterId Ledger) : IRequest<bool>;


public sealed class Gate : Actor<GateId, GateState>
{
    [Handle]
    public async Reply<bool> On(EnterAwaited m)
    {
        State.Entered++;
        await Cluster.Ask(m.Ledger, new Add(1));
        return true;
    }

    [Handle]
    public Reply<bool> On(EnterDeferred m)
    {
        State.Entered++;
        return ReplyWhen(Cluster.Ask(m.Ledger, new Add(1)), true);
    }
}

// ---------------------------------------------------------------- delayed sends

/// <summary>Send <see cref="Count"/> one-way bumps to the target, now (Send) or through the shard's timers (SendAfter).</summary>
[NinoType, Message] public partial record struct Bumps(CounterId Target, int Count, bool Delayed) : IRequest<bool>;

public readonly record struct SchedulerId(long Value) : IActorId;

public sealed class Scheduler : Actor<SchedulerId>
{
    [Handle]
    public bool On(Bumps m)
    {
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Delayed) Cluster.SendAfter(TimeSpan.Zero, m.Target, new Bumped(1));
            else Cluster.Send(m.Target, new Bumped(1));
        }

        return true;
    }
}
