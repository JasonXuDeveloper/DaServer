using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- an actor type with a small mailbox

public readonly record struct NarrowId(long Value) : IActorId;

[NinoType, Message] public partial record struct SlowStep : IRequest<bool>;

public sealed class Narrow : Actor<NarrowId>
{
    public override int MailboxCapacity => 2;

    [Handle]
    public async Reply<bool> On(SlowStep _)
    {
#pragma warning disable DAS009 // on purpose: requests pile up behind a slow turn
        await Task.Delay(10);
#pragma warning restore DAS009
        return true;
    }
}
