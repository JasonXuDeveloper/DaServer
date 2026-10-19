using System.Runtime.CompilerServices;
using EasyEcs;
using Nino.Core;

namespace DaServer.Benchmark;

// Benchmark business code, written like user code (ADR-080: static classes of stateless handlers, data in components):
// an actor with state in a component that also takes one-way messages, a room actor whose groups clients join through a
// lobby service, and a message pushed to clients. Every actor type is [Sticky]: a benchmark places its actors on purpose
// (Remote means another node), so load balancing must not move them mid-run.

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


[Sticky, Actor<CounterId>]
public static class Counter
{
    [Handle] internal static int On(in Add m, ref CounterState s) => s.Value += m.Delta;

    [Handle] internal static void On(in Bumped e, ref CounterState s) => s.Value += e.By;
}

[Service]
public static class Lobby
{
    /// <summary>The room answers the client: the reply waits as data while the lobby takes its next caller.</summary>
    [Handle]
    internal static Answer<bool> On(in Join m, in Context ctx)
    {
        ctx.Ask(m.Room, new Admit(ctx.Caller, m.Groups), ctx.ReplyLater());
        return Answer<bool>.Later;
    }

    [Handle]
    internal static void On(in Answered<Admit, bool> a, in Context ctx)
    {
        if (a.Ok) ctx.Reply(a.ReplyTo, a.Value);
        else ctx.Fail(a.ReplyTo, a.Status);
    }
}

[Sticky, Actor<RoomId>]
public static class Room
{
    [Handle]
    internal static bool On(in Admit m, in Context ctx)
    {
        for (int i = 0; i < m.Groups; i++) ctx.Send(m.Session, new JoinGroup(GroupOf(i, in ctx)));
        return true;
    }

    [Handle]
    internal static bool On(in Broadcast m, in Context ctx)
    {
        for (int i = 0; i < m.Groups; i++) ctx.Send(GroupOf(i, in ctx), new Tick(m.Sequence));
        return true;
    }

    private static ClientGroup GroupOf(int index, in Context ctx) => index == 0 ? ctx.Group : ClientGroup.Of<Cells>(ctx.Key * 16 + index);
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

/// <summary>
/// Enter through the gate, answered when the ledger's acknowledgement arrives (Answered): the gate passes the caller's
/// reply token along with its ask and replies from the answer's handler.
/// </summary>
[NinoType, Message] public partial record struct EnterAnswered(CounterId Ledger) : IRequest<bool>;

/// <summary>The same, replied by the runtime when the acknowledgement arrives (ReplyAfter).</summary>
[NinoType, Message] public partial record struct EnterReplyAfter(CounterId Ledger) : IRequest<bool>;


[Sticky, Actor<GateId>]
public static class Gate
{
    [Handle]
    internal static Answer<bool> On(in EnterAnswered m, ref GateState s, in Context ctx)
    {
        s.Entered++;
        ctx.Ask(m.Ledger, new Add(1), ctx.ReplyLater());
        return Answer<bool>.Later;
    }

    [Handle]
    internal static void On(in Answered<Add, int> a, in Context ctx)
    {
        if (a.Ok) ctx.Reply(a.ReplyTo, true);
        else ctx.Fail(a.ReplyTo, a.Status);
    }

    [Handle]
    internal static Answer<bool> On(in EnterReplyAfter m, ref GateState s, in Context ctx)
    {
        s.Entered++;
        return ctx.ReplyAfter(ctx.Ask(m.Ledger, new Add(1)), true);
    }
}

// ---------------------------------------------------------------- delayed sends

/// <summary>Send <see cref="Count"/> one-way bumps to the target, now (Send) or through the shard's timers (SendAfter).</summary>
[NinoType, Message] public partial record struct Bumps(CounterId Target, int Count, bool Delayed) : IRequest<bool>;

public readonly record struct SchedulerId(long Value) : IActorId;

[Sticky, Actor<SchedulerId>]
public static class Scheduler
{
    [Handle]
    internal static bool On(in Bumps m, in Context ctx)
    {
        for (int i = 0; i < m.Count; i++)
        {
            if (m.Delayed) ctx.SendAfter(TimeSpan.Zero, m.Target, new Bumped(1));
            else ctx.Send(m.Target, new Bumped(1));
        }

        return true;
    }
}

// ---------------------------------------------------------------- persistence (PersistenceBenchmark)

public readonly record struct VaultId(long Value) : IActorId;

[NinoType, Message] public partial record struct Put(long Amount) : IRequest<long>;

/// <summary>Holds coins ([Persist]); a put is a critical change, written at once (SaveNow), with no periodic checkpoint.</summary>
[Sticky, Actor<VaultId>(SaveEverySeconds = 0)]
public static class Vault
{
    [Handle]
    internal static long On(in Put m, ref Coins coins, Entity self)
    {
        if (m.Amount == 0) return coins.Amount;
        self.Set(new SaveNow());
        return coins.Amount += m.Amount;
    }
}
