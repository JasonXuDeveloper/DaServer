namespace DaServer.Protocol;

/// <summary>
/// Status carried by <see cref="FrameKind.Reply"/> frames. A refusal at the edge may carry when to retry, in milliseconds,
/// in the reply's <see cref="FrameHeader.Key"/> (0: no hint).
/// </summary>
public enum RpcStatus : byte
{
    Ok = 0,
    /// <summary>Handler threw; payload = message string.</summary>
    Error = 1,
    /// <summary>Target actor/service not hosted here (stale membership view); the caller should retry.</summary>
    NotHosted = 3,
    Timeout = 4,
    /// <summary>Client may not send this message, may not name this actor (not granted) or has no actor bound for it.</summary>
    Forbidden = 5,
    /// <summary>Connection lost before the reply arrived.</summary>
    Disconnected = 6,
    /// <summary>The target shard or actor mailbox is full; nothing ran. The caller may back off and retry.</summary>
    Overloaded = 7,
    /// <summary>The edge's admission refused the sender for now (a rate limit); nothing ran. Retry after the hint.</summary>
    RateLimited = 8,
    /// <summary>
    /// The request reached an actor whose running turn waits for it (A asks B, B asks A): it would wait until the
    /// timeout, so it fails at once. Nothing ran. Model such flows with one-way messages.
    /// </summary>
    CallCycle = 9,

    /// <summary>
    /// The request's payload is not the message it names (truncated, written by another build, hostile): nothing ran.
    /// Retrying the same bytes cannot help.
    /// </summary>
    Malformed = 10,

    /// <summary>
    /// A resume named a session that another connection resumed already (a ticket presented twice at once): the actor
    /// keeps the session that resumed first.
    /// </summary>
    Superseded = 11,
}
