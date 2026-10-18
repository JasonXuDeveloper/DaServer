namespace DaServer.Runtime;

/// <summary>
/// The runtime's logs, in one place: source-generated, so a disabled level costs a check and an enabled one formats
/// nothing up front and boxes nothing (paths that can repeat at volume: a message's failure, a dropped frame, a link).
/// </summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "{Kind} {Id:x8} failed in {Target}")]
    public static partial void MessageFailed(ILogger logger, Exception error, FrameKind kind, uint id, object target);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Step} of {Actor} failed")]
    public static partial void ActorFailed(ILogger logger, Exception error, string step, object actor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A turn of {Actor} has run for {Ms} ms, past the call timeout: the actor takes no mail until it ends (an await that never completes?)")]
    public static partial void TurnTooLong(ILogger logger, object actor, long ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link {Host}:{Port} backlog full: {Count} frames dropped")]
    public static partial void BacklogFull(ILogger logger, string host, int port, int count);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Shard {Shard} crashed")]
    public static partial void ShardCrashed(ILogger logger, Exception error, int shard);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in shard {Shard}")]
    public static partial void ShardFailed(ILogger logger, Exception error, int shard);

    [LoggerMessage(Level = LogLevel.Error, Message = "Service call failed")]
    public static partial void ServiceCallFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a link from {Host}:{Port} ({Node:x}): {Reason}")]
    public static partial void LinkRefused(ILogger logger, string host, int port, ulong node, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Name} ({Id:x}) up on {Endpoint}, {Shards} shards; actors: {Actors}; services: {Services}; systems: {Systems}")]
    public static partial void NodeUp(ILogger logger, string name, ulong id, System.Net.IPEndPoint endpoint, int shards, string actors, string services, string systems);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Name} ({Id:x}) stopped")]
    public static partial void NodeStopped(ILogger logger, string name, ulong id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stop {Phase} of {Node}: {Ms} ms")]
    public static partial void StopPhase(ILogger logger, string phase, string node, long ms);

    [LoggerMessage(Level = LogLevel.Error, Message = "Shutdown budget ({Budget}) exceeded: shard {Shard} of {Node} still holds {Actors} actors whose OnUnload did not finish (their state since the last save is lost)")]
    public static partial void BudgetExceeded(ILogger logger, TimeSpan budget, int shard, string node, int actors);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Node {Name} reaches {Reachable} of the cluster's members and lost {Lost}: on the minority side of a split, it stops without saving and exits")]
    public static partial void MinoritySide(ILogger logger, string name, int reachable, int lost);

    [LoggerMessage(Level = LogLevel.Information, Message = "The cluster runs {Count} build(s): {Builds}")]
    public static partial void Builds(ILogger logger, int count, string builds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client-facing node {Node:x} is gone: closing its sessions")]
    public static partial void ClientNodeGone(ILogger logger, ulong node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shard {Shard} of {Node} has been in one pass for {Ms} ms: a handler or system blocks its thread (a synchronous wait, DAS009, or a long loop) and its actors wait")]
    public static partial void ShardStalled(ILogger logger, int shard, string node, long ms);

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance failed")]
    public static partial void MaintenanceFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link {Host}:{Port} unreachable, dropping its stream")]
    public static partial void LinkUnreachable(ILogger logger, Exception error, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Link {Host}:{Port}: peer restarted, stream restarted")]
    public static partial void LinkPeerRestarted(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link {Host}:{Port}: stream could not resume, restarted")]
    public static partial void LinkRestarted(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Link {Host}:{Port} closed ({Reason}), resuming")]
    public static partial void LinkClosed(ILogger logger, string host, int port, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gossip round failed")]
    public static partial void GossipFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Membership round ran {Ms} ms late (a pause of this process): failure verdicts deferred")]
    public static partial void RoundLate(ILogger logger, long ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Member {Member} is dead")]
    public static partial void MemberDead(ILogger logger, string member);

    [LoggerMessage(Level = LogLevel.Information, Message = "Member {Member} is suspect")]
    public static partial void MemberSuspect(ILogger logger, string member);

    [LoggerMessage(Level = LogLevel.Information, Message = "Member {Member} joined ({Status})")]
    public static partial void MemberJoined(ILogger logger, string member, MemberStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Declared dead by the cluster: removed local actors before rejoining")]
    public static partial void DeclaredDead(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Seed {Seed} names no node yet")]
    public static partial void SeedUnresolved(ILogger logger, string seed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed {Seed} cannot be resolved")]
    public static partial void SeedFailed(ILogger logger, Exception error, string seed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Loading {Count} records failed: their callers get Error")]
    public static partial void LoadFailed(ILogger logger, Exception error, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring the record of {Actor} failed")]
    public static partial void RestoreFailed(ILogger logger, Exception error, object actor);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving {Count} records failed: retried in a second")]
    public static partial void SaveFailed(ILogger logger, Exception error, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The record of {Actor} was written by another node (expected version {Version}): this node no longer owns it, its copy is dropped")]
    public static partial void RecordTaken(ILogger logger, object actor, long version);

    [LoggerMessage(Level = LogLevel.Error, Message = "The record handed over for {Actor} could not be read: it loads from the store")]
    public static partial void HandoffUnreadable(ILogger logger, Exception error, object actor);
}
