using System;

namespace DaServer.Rpc;

/// <summary>
/// Address of a client session: the gateway node that holds the socket plus the session id on that gateway.
/// Serializable, so actors can store it in components and push to the client later.
/// </summary>
[Nino.Core.NinoType]
public partial struct SessionRef : IEquatable<SessionRef>
{
    public ulong GatewayNodeId;
    public long SessionId;

    public SessionRef(ulong gatewayNodeId, long sessionId)
    {
        GatewayNodeId = gatewayNodeId;
        SessionId = sessionId;
    }

    public readonly bool IsValid => SessionId != 0;

    public readonly bool Equals(SessionRef other) => GatewayNodeId == other.GatewayNodeId && SessionId == other.SessionId;

    public override readonly bool Equals(object? obj) => obj is SessionRef other && Equals(other);

    public override readonly int GetHashCode() => (GatewayNodeId.GetHashCode() * 397) ^ SessionId.GetHashCode();

    public override readonly string ToString() => $"Session({GatewayNodeId:x}:{SessionId})";

    public static bool operator ==(SessionRef a, SessionRef b) => a.Equals(b);

    public static bool operator !=(SessionRef a, SessionRef b) => !a.Equals(b);
}
