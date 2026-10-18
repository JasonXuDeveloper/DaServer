namespace DaServer.Persistence;

// The handoff (ADR-072) as ECS data: where an actor's record is going or coming from, deferred requests for records,
// the views a node replaced lately.

/// <summary>The actor leaves for node <see cref="To"/>, which owns it now: its record goes there once no write is in flight.</summary>
internal readonly record struct HandOff(ulong To) : IComponent;

/// <summary>
/// The record went to <see cref="To"/>: the actor is removed (its mail forwarded) once it accepted, or saved to the
/// store when it refused or did not answer by <see cref="UntilMs"/>.
/// The copy holds no state meanwhile (no system sees it): <see cref="Record"/> (the record frame's payload) restores it for
/// that save, and goes again when <see cref="To"/> asks for it or does not answer while alive (a link that restarted its
/// stream lost it).
/// </summary>
internal readonly record struct HandingOff(ulong To, long UntilMs, byte[] Record) : IComponent;

/// <summary>A critical write (<see cref="SaveNow"/>) failed on a copy leaving by handoff: its new owner writes it at once.</summary>
internal struct CriticalPending : IComponent;

/// <summary>The copy's record was taken by node <see cref="To"/>: a node that asked this one for it is sent there.</summary>
internal readonly record struct HandedTo(ulong To) : IComponent;

/// <summary>The handoff (its record's id) a copy was restored from: the same record again is answered "accepted".</summary>
internal readonly record struct TakenHandoff(ulong Id) : IComponent;

/// <summary>
/// The handoffs this shard took or passed on lately, by the id each record carries (a resend carries its first send's):
/// a resend whose first send was taken here is answered "accepted" again, wherever the actor is now; one passed on goes
/// to the same node again. Kept for two handoff timeouts (the resends' window), oldest first out past <see cref="Max"/>;
/// until the evicted ones would have expired (<see cref="ForgotUntilMs"/>), "not seen" no longer means "never here".
/// </summary>
internal struct HandoffsSeen() : ISingletonComponent
{
    public const int Max = 65_536; // about 3 MB at most per shard: a join that moves more within 20 s forgets the oldest

    public readonly Dictionary<ulong, SeenHandoff> ById = new();
    public readonly Queue<(ulong Id, long UntilMs)> Order = new();
    public long ForgotUntilMs;
}

/// <summary>A handoff taken here (<see cref="PassedTo"/> 0) or passed on to <see cref="PassedTo"/>, remembered until <see cref="UntilMs"/>.</summary>
internal readonly record struct SeenHandoff(ulong PassedTo, long UntilMs);

/// <summary>
/// The actor moved here from <see cref="From"/>, which may still hold it: its mail waits for the record, or for word that
/// there is none, until <see cref="UntilMs"/>; then it loads from the store.
/// </summary>
internal readonly record struct AwaitingHandoff(ulong From, long UntilMs) : IComponent;

/// <summary>Node <see cref="By"/> waits for this actor's record: told when the actor goes without handing it to it.</summary>
internal readonly record struct HandoffWanted(ulong By) : IComponent;

/// <summary>
/// Requests for records this shard holds no copy of but must not answer "none" yet (<see cref="Handoff.MustWait"/>): it
/// owns their keys in its own view (it has not seen the change the askers saw yet), or a record may still reach it from
/// the node it took the key from (it goes on to the asker). Answered once ownership changes
/// (<see cref="HandoffAsksSystem"/>) or by the record. Bounded: past <see cref="Max"/> a request goes unanswered, and its
/// asker asks again after the handoff timeout.
/// </summary>
internal readonly record struct HandoffAsks(List<HandoffAsk> Waiting) : ISingletonComponent
{
    public const int Max = 1024;
}

/// <summary>A deferred request of <see cref="By"/>; dropped unanswered after <see cref="UntilMs"/> (its asker asks again).</summary>
internal readonly record struct HandoffAsk(uint HandlerTypeId, long Key, ulong By, long UntilMs);

/// <summary>A view this node replaced at <see cref="ReplacedMs"/> (real time, <see cref="Node.RecentViews"/>).</summary>
internal readonly record struct RecentView(ClusterView View, long ReplacedMs)
{
    public const int Kept = 16;

    /// <summary>The views to keep once <paramref name="replaced"/> was replaced now: those replaced within the move window.</summary>
    public static RecentView[] Push(RecentView[] views, ClusterView replaced, NodeOptions options)
    {
        long now = Platform.Milliseconds, window = Handoff.MoveWindowMs(options);
        int kept = 0;
        while (kept < views.Length && kept < Kept - 1 && now - views[kept].ReplacedMs <= window) kept++;
        var next = new RecentView[kept + 1]; // cold: one per view change
        next[0] = new RecentView(replaced, now);
        Array.Copy(views, 0, next, 1, kept);
        return next;
    }
}
