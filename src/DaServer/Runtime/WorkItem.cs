namespace DaServer.Runtime;

internal enum WorkKind : byte
{
    /// <summary>A = FrameWriter (request / one-way frame), B = IReplySink (where the response goes).</summary>
    Dispatch = 1,

    /// <summary>A = IPendingCall, B = FrameWriter (response frame). Completes a call made from this shard.</summary>
    Complete = 2,

    /// <summary>A = Action&lt;object?&gt;, B = state. Continuations and runtime callbacks.</summary>
    Callback = 3,

    /// <summary>A = <see cref="TurnWatcher"/>: a turn's task completed.</summary>
    TurnEnded = 4,
}

/// <summary>Mailbox entry: a 24-byte struct (two references + kind), no allocation per message.</summary>
internal readonly struct WorkItem
{
    public readonly object? A;
    public readonly object? B;
    public readonly WorkKind Kind;

    public WorkItem(WorkKind kind, object? a, object? b)
    {
        Kind = kind;
        A = a;
        B = b;
    }
}
