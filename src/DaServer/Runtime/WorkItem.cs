namespace DaServer.Runtime;

internal enum WorkKind : byte
{
    /// <summary>A = FrameWriter (request / one-way frame), B = IReplySink (where the reply goes).</summary>
    Dispatch = 1,

    /// <summary>A = IPendingCall, B = FrameWriter (reply frame). Completes a call made from this shard.</summary>
    Complete = 2,

    /// <summary>A = Action&lt;object?&gt;, B = state. Runtime callbacks posted from other threads.</summary>
    Callback = 3,
}

/// <summary>Mailbox entry: a 24-byte struct (two references + kind), no allocation per message.</summary>
internal readonly record struct WorkItem(WorkKind Kind, object? A, object? B);
