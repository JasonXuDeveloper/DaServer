namespace DaServer.Runtime;

internal enum WorkKind : byte
{
    /// <summary>A = FrameWriter (request / one-way frame), B = IReplySink (where the reply goes).</summary>
    Dispatch = 1,

    /// <summary>A = IPendingCall, B = FrameWriter (reply frame). Completes a call made from this shard.</summary>
    Complete = 2,

    /// <summary>A = Action&lt;object?&gt;, B = state. Continuations and runtime callbacks.</summary>
    Callback = 3,

    /// <summary>A = <see cref="TurnWatcher"/>: a turn's task completed.</summary>
    TurnEnded = 4,
}

/// <summary>Mailbox entry: a 24-byte struct (two references + kind), no allocation per message.</summary>
internal readonly record struct WorkItem(WorkKind Kind, object? A, object? B);
