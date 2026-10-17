namespace DaServer.Runtime;

/// <summary>
/// The runtime's logs on paths that can repeat at volume (a message's failure, a dropped frame): source-generated, so a
/// disabled level costs a check and an enabled one formats nothing up front and boxes nothing.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "{Kind} {Id:x8} failed in {Target}")]
    public static partial void MessageFailed(ILogger logger, Exception error, FrameKind kind, uint id, object target);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Step} of {Actor} failed")]
    public static partial void ActorFailed(ILogger logger, Exception error, string step, object actor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link {Host}:{Port} backlog full: {Count} frames dropped")]
    public static partial void BacklogFull(ILogger logger, string host, int port, int count);
}
