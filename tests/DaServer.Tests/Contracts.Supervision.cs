using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- supervision (OnError)

public readonly record struct FragileId(long Value) : IActorId;


/// <summary>Add, then fail half-way when <see cref="Fail"/> (synchronously or after an await): the state is half-changed.</summary>
[NinoType, Message] public partial record struct Spoil(int Delta, bool Fail, bool Async) : IRequest<int>;

/// <summary>Keys from 100 reload after a failure, the others resume (the default).</summary>
public sealed class Fragile : Actor<FragileId, FragileState>
{
    [Handle]
    public async Reply<int> On(Spoil m)
    {
        State.Value += m.Delta;
        if (m.Async) await Cluster.Ask(new CounterId(Key), new Get());
        if (m.Fail) throw new InvalidOperationException("half-applied");
        return State.Value;
    }

    public override ErrorAction OnError(Exception error) => Key >= 100 ? ErrorAction.Reload : ErrorAction.Resume;
}
