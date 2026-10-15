namespace DaServer.Rpc;

/// <summary>Failure of a remote call.</summary>
public sealed class RpcException : Exception
{
    public RpcException(RpcStatus status, string? message = null)
        : base(message ?? status.ToString())
    {
        Status = status;
    }

    public RpcStatus Status { get; }

    /// <summary>The failure carried by an error response (its payload is the Nino-serialized message, if any).</summary>
    public static RpcException FromPayload(RpcStatus status, ReadOnlySpan<byte> payload)
    {
        string? message = null;
        if (!payload.IsEmpty)
        {
            try
            {
                var reader = new Reader(payload);
                NinoDeserializer.Deserialize(out message, ref reader);
            }
            catch
            {
                // malformed error payload: keep status only
            }
        }

        return new RpcException(status, message);
    }
}
