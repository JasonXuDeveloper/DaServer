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

    public override readonly int GetHashCode() => HashCode.Combine(GatewayNodeId, SessionId);

    public override readonly string ToString() => $"Session({GatewayNodeId:x}:{SessionId})";

    public static bool operator ==(SessionRef a, SessionRef b) => a.Equals(b);

    public static bool operator !=(SessionRef a, SessionRef b) => !a.Equals(b);
}

/// <summary>
/// A broadcast group of client sessions spanning every gateway (map instance, AOI cell, guild channel, chat room).
/// A push to a group is serialized once and sent as one frame per gateway, which fans it out to its local members.
/// </summary>
[Nino.Core.NinoType]
public readonly partial record struct ClientGroup(long Id)
{
    /// <summary>Every connected session of the cluster.</summary>
    public static readonly ClientGroup All = new(0);
}
