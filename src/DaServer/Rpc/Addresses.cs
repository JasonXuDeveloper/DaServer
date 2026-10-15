namespace DaServer.Rpc;

/// <summary>
/// Address of a client session: the gateway node that holds the socket plus the session id on that gateway.
/// Serializable, so actors can store it in components and push to the client later.
/// </summary>
[Nino.Core.NinoType]
public readonly partial record struct SessionRef(ulong GatewayNodeId, long SessionId)
{
    public bool IsValid => SessionId != 0;

    public override string ToString() => $"Session({GatewayNodeId:x}:{SessionId})";
}

/// <summary>
/// A broadcast group of client sessions spanning every gateway (map instance, AOI cell, guild channel, chat room).
/// A push to a group is serialized once and sent as one frame per gateway, which fans it out to its local members.
/// Every actor has its own group (<c>Actor.Group</c>), so group ids of different features never collide.
/// </summary>
[Nino.Core.NinoType]
public readonly partial record struct ClientGroup(long Id)
{
    /// <summary>Every connected session of the cluster.</summary>
    public static readonly ClientGroup All = new(0);

    /// <summary>
    /// The group of <paramref name="key"/> within the scope <typeparamref name="TScope"/>, any type naming a feature
    /// (<c>ClientGroup.Of&lt;MapGrid&gt;(cell)</c>).
    /// </summary>
    public static ClientGroup Of<TScope>(long key) => Of(TypeId<TScope>.Value, key);

    /// <summary>The <c>Group</c> of actor <paramref name="id"/> (<c>ClientGroup.Of(new RoomId(7))</c>).</summary>
    public static ClientGroup Of<TId>(TId id) where TId : struct, IActorId => Of(ActorIds<TId>.HandlerTypeId, id.Value);

    /// <summary>The group of <paramref name="key"/> within <paramref name="scope"/>: a 64-bit mix of both, never <see cref="All"/>.</summary>
    internal static ClientGroup Of(uint scope, long key)
    {
        ulong x = ((ulong)scope << 32 | scope) ^ (ulong)key;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL; // splitmix64 finalizer: neighbouring keys spread over the range
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return new ClientGroup(x == 0 ? 1 : (long)x);
    }
}

/// <summary>Where a client should connect: a gateway's public host and client port (answer of a balancer).</summary>
[Nino.Core.NinoType]
public readonly partial record struct GatewayEndpoint(string Host, int Port);
