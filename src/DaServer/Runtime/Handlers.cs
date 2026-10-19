namespace DaServer.Runtime;

// The programming model (ADR-081): an actor is an entity, its data is components, its behavior is stateless static
// handlers. Nothing here holds state: attributes are constants, Context is a value built per message, ReplyTo and
// Answered are data.

/// <summary>
/// Names the actor type addressed by <typeparamref name="TId"/>: a static class of <see cref="HandleAttribute">[Handle]</see>
/// methods (<c>[Actor&lt;PlayerId&gt;] public static class Player { [Handle] static void On(in GetInfo m, ..) .. }</c>).
/// Its data is the components of its entity; its settings are these constants.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ActorAttribute<TId> : Attribute where TId : struct, IActorId
{
    /// <summary>Unload the actor after this many seconds without mail (0: stays loaded until it moves or the node stops).</summary>
    public int UnloadAfterIdleSeconds { get; init; }

    /// <summary>
    /// Write the actor's changed stored components at most this often (seconds; ADR-068). A change that must survive a
    /// crash asks for a write at once with <see cref="SaveNow"/>.
    /// </summary>
    public int SaveEverySeconds { get; init; } = 30;

    /// <summary>Mail held for the actor while it is not ready, beyond which senders are refused (0: the node's default).</summary>
    public int MailboxCapacity { get; init; }

    /// <summary>What a handler that throws does to the actor (C4).</summary>
    public ErrorAction OnError { get; init; }
}

/// <summary>
/// Names a service: a static class of <see cref="HandleAttribute">[Handle]</see> methods without an entity of its own,
/// one per shard of the nodes hosting it. Its state, if any, is singletons of the shard's world.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ServiceAttribute : Attribute;

/// <summary>What happens to an actor whose handler threw.</summary>
public enum ErrorAction
{
    /// <summary>Keep the actor and its components; the next message runs.</summary>
    Resume,

    /// <summary>Remove the actor without saving (its components may be half-changed); the next message loads it again.</summary>
    Reload,
}

/// <summary>
/// Tag: write the actor's record now (ADR-068), for a change that must survive a crash (a trade, a purchase):
/// <c>self.SetSaveNow()</c>. Removed once the write is queued; an unchanged record is not written.
/// </summary>
public struct SaveNow : IComponent;

/// <summary>Sent to an actor that has no stored or moved record: its handler of it gives the actor its first data.</summary>
[NinoType, Message] public readonly partial record struct Created;

/// <summary>Sent to an actor whose stored or moved record was applied, before its other mail.</summary>
[NinoType, Message] public readonly partial record struct Loaded;

/// <summary>The last mail of an actor that leaves its shard (it moves, idles out or the node stops); its record is written after it.</summary>
[NinoType, Message] public readonly partial record struct Unloading;

/// <summary>
/// Where a request waits for its reply, as data (unmanaged): keep it in a component to reply later
/// (<c>ctx.Reply(pending.Client, value)</c>). The request is an entity of the shard's world while it waits
/// (<see cref="PendingReply"/>): a token whose entity is gone (replied, or its caller stopped waiting) replies to nobody.
/// <c>default</c> when nobody waits (a one-way message).
/// </summary>
public readonly record struct ReplyTo(EntityId Pending, uint CallId)
{
    /// <summary>Whether a caller waits for this reply.</summary>
    public bool Expected => CallId != 0;
}

/// <summary>
/// The answer to <c>ctx.Ask(address, request)</c>, delivered to the asking actor as a message: the reply
/// (<see cref="Value"/>), or why there is none (<see cref="Status"/>: refused, failed, timed out). <see cref="Call"/> is
/// what <c>Ask</c> returned, to tell several asks of one type apart; <see cref="ReplyTo"/> is the request the asker was
/// handling when it asked, if it passed it along (<c>ctx.Ask(target, request, ctx.ReplyLater())</c>), so the answer can
/// reply to it without keeping anything in a component.
/// </summary>
public readonly record struct Answered<TRequest, TReply>(uint Call, RpcStatus Status, TReply Value, ReplyTo ReplyTo)
    where TRequest : struct, IRequest<TReply>
{
    public bool Ok => Status == RpcStatus.Ok;
}
