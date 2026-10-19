using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- an actor type with a small mailbox

public readonly record struct NarrowId(long Value) : IActorId;

[NinoType, Message] public partial record struct Step : IRequest<bool>;

/// <summary>Holds at most two messages while it is not live (its record loading): more are refused.</summary>
[Actor<NarrowId>(MailboxCapacity = 2)]
public static class Narrow
{
    [Handle] internal static bool On(in Step _) => true;
}
