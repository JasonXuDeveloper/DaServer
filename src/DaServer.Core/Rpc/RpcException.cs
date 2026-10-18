namespace DaServer.Rpc;

/// <summary>
/// Failure of a remote call. Refusals by the runtime (<see cref="RpcStatus.NotHosted"/>, <see cref="RpcStatus.Overloaded"/>,
/// <see cref="RpcStatus.Timeout"/>, ...) travel as status-only frames: their <see cref="Message"/> is a constant
/// description of the status. Only a failing handler sends a message of its own (<see cref="Detail"/>).
/// </summary>
public sealed class RpcException(RpcStatus status, string? message = null, TimeSpan retryAfter = default) : Exception
{
    /// <summary>When the edge that refused the call says it may be retried (zero: no hint).</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;

    public RpcStatus Status { get; } = status;

    /// <summary>The message sent with the failure (a handler's error), or null for a status-only failure.</summary>
    public string? Detail { get; } = message;

    public override string Message => Detail ?? Describe(Status);

    /// <summary>The failure carried by an error reply (its payload is the Nino-serialized message, if any).</summary>
    public static RpcException FromPayload(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        string? message = null;
        if (!payload.IsEmpty)
        {
            try
            {
                message = NinoDeserializer.Deserialize<string>(payload);
            }
            catch
            {
                // malformed error payload: keep status only
            }
        }

        return new RpcException(status, message);
    }

    /// <summary>Constant description of a status (no formatting: refusals happen exactly under load).</summary>
    public static string Describe(RpcStatus status) => status switch
    {
        RpcStatus.Ok => "Ok",
        RpcStatus.Error => "Error: the handler failed",
        RpcStatus.NotHosted => "NotHosted: no node hosts the target (stale view, nothing registered, or too many forwarding hops)",
        RpcStatus.Timeout => "Timeout: no reply within the call's deadline",
        RpcStatus.Forbidden => "Forbidden: a client may not send this message to this target (not a client message, " +
                               "not that actor, no bound actor, or an invalid resume ticket)",
        RpcStatus.Disconnected => "Disconnected: the connection or the target's node was lost before the reply",
        RpcStatus.Overloaded => "Overloaded: a dispatch queue, actor mailbox or in-flight call table is full; nothing ran",
        RpcStatus.RateLimited => "RateLimited: the edge's admission refused the sender for now; nothing ran",
        RpcStatus.CallCycle => "CallCycle: the target actor's running turn waits for this request (a call cycle); nothing ran. " +
                               "Use a one-way message for one direction",
        _ => "Unknown status",
    };
}
