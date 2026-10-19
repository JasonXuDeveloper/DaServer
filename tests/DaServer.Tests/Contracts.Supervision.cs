using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- supervision (OnError)

public readonly record struct FragileId(long Value) : IActorId;
public readonly record struct ReloadingFragileId(long Value) : IActorId;

/// <summary>
/// Add, then fail when <see cref="Fail"/>: in its own handler, or (<see cref="Async"/>) in the handler of the answer to
/// an ask it made first. The state is half-changed either way.
/// </summary>
[NinoType, Message] public partial record struct Spoil(int Delta, bool Fail, bool Async) : IRequest<int>;

/// <summary>Resumes after a failure (the default): it keeps its half-changed state.</summary>
[Actor<FragileId>]
public static class Fragile
{
    [Handle] internal static Answer<int> On(in Spoil m, ref FragileState state, ref Spoiling spoiling, in Context ctx) =>
        Spoils.Begin(in m, ref state, ref spoiling, in ctx);

    [Handle] internal static void On(in Answered<Get, int> a, ref FragileState state, in Spoiling spoiling, in Context ctx) =>
        Spoils.End(in a, ref state, in spoiling, in ctx);
}

/// <summary>Reloads after a failure: removed without saving, loaded again by its next mail.</summary>
[Actor<ReloadingFragileId>(OnError = ErrorAction.Reload)]
public static class ReloadingFragile
{
    [Handle] internal static Answer<int> On(in Spoil m, ref FragileState state, ref Spoiling spoiling, in Context ctx) =>
        Spoils.Begin(in m, ref state, ref spoiling, in ctx);

    [Handle] internal static void On(in Answered<Get, int> a, ref FragileState state, in Spoiling spoiling, in Context ctx) =>
        Spoils.End(in a, ref state, in spoiling, in ctx);
}

/// <summary>The spoil behavior both fragile types share.</summary>
internal static class Spoils
{
    public static Answer<int> Begin(in Spoil m, ref FragileState state, ref Spoiling spoiling, in Context ctx)
    {
        if (m.Async)
        {
            spoiling = new Spoiling(m.Delta, m.Fail); // what the answer's handler applies
            ctx.Ask(new CounterId(ctx.Key), new Get(), ctx.ReplyLater());
            return Answer<int>.Later;
        }

        state.Value += m.Delta;
        if (m.Fail) throw new InvalidOperationException("half-applied");
        return state.Value;
    }

    public static void End(in Answered<Get, int> a, ref FragileState state, in Spoiling spoiling, in Context ctx)
    {
        if (!a.Ok)
        {
            ctx.Fail(a.ReplyTo, a.Status);
            return;
        }

        state.Value += spoiling.Delta;
        if (spoiling.Fail) throw new InvalidOperationException("half-applied");
        ctx.Reply(a.ReplyTo, state.Value);
    }
}
