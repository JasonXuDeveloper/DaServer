using System.Runtime.CompilerServices;

namespace DaServer.Rpc;

/// <summary>
/// Who receives a message: a struct whose type says how it is routed. <see cref="IActorId"/> (one actor, placed by
/// its id), <see cref="INodeAddress"/> (something held by one node, such as a client session) or
/// <see cref="IGroupAddress"/> (a group spread over the nodes that have members). <c>Cluster.Send(address, m)</c>
/// takes any of them; the type is resolved once per address type, never per message, and nothing is boxed.
/// </summary>
public interface IAddress;

/// <summary>
/// Something one node holds (a client session on its gateway): a struct of exactly a <c>ulong</c> node id then a
/// <c>long</c> value, in that order.
/// </summary>
public interface INodeAddress : IAddress
{
    /// <summary>The node holding it.</summary>
    ulong Node { get; }

    /// <summary>Which one on that node (session id, ...).</summary>
    long Value { get; }
}

/// <summary>
/// A group whose members are spread over nodes (a client group): a struct of exactly one <c>long</c>. A message to it
/// is serialized once and sent as one frame per node that may have members, which fans it out locally.
/// </summary>
public interface IGroupAddress : IAddress
{
    /// <summary>Which group.</summary>
    long Value { get; }
}

/// <summary>
/// Who sent a message from outside the cluster: the node whose ingress let it in and its id there (today a client
/// session on its gateway). Read in handlers as <c>Caller</c> (default for messages sent inside the cluster), and an
/// address: <c>Cluster.Send(Caller, m)</c> reaches it (<c>[ToClient]</c> messages).
/// </summary>
[Nino.Core.NinoType]
public readonly partial record struct Origin(ulong Node, long Id) : INodeAddress
{
    /// <summary>Bytes of an origin trailer.</summary>
    internal const int Size = sizeof(ulong) + sizeof(long);

    /// <summary>False for messages that started inside the cluster (a system, a timer, a server call).</summary>
    public bool IsValid => Id != 0;

    ulong INodeAddress.Node => Node;

    long INodeAddress.Value => Id;

    public override string ToString() => $"Origin({Node:x}:{Id})";
}

/// <summary>
/// Address of a client session: the gateway node that holds the socket plus the session id on that gateway.
/// Serializable, so actors can store it in components and message the client later (<c>Cluster.Send(session, m)</c>).
/// </summary>
[Nino.Core.NinoType]
public readonly partial record struct SessionRef(ulong GatewayNodeId, long SessionId) : INodeAddress
{
    public bool IsValid => SessionId != 0;

    /// <summary>The session a client message came from (<c>BindSession(Caller)</c>, a component field).</summary>
    public static implicit operator SessionRef(Origin origin) => new(origin.Node, origin.Id);

    ulong INodeAddress.Node => GatewayNodeId;

    long INodeAddress.Value => SessionId;

    public override string ToString() => $"Session({GatewayNodeId:x}:{SessionId})";
}

/// <summary>
/// A broadcast group of client sessions spanning every gateway (map instance, AOI cell, guild channel, chat room).
/// A push to a group is serialized once and sent as one frame per gateway, which fans it out to its local members.
/// Every actor has its own group (<c>Actor.Group</c>), so group ids of different features never collide.
/// </summary>
[Nino.Core.NinoType]
public readonly partial record struct ClientGroup(long Id) : IGroupAddress
{
    long IGroupAddress.Value => Id;

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

/// <summary>
/// Address scopes: the <see cref="FrameHeader.Target"/> of messages to sessions and client groups (the value is the
/// header's key). Handler type ids and scopes are type ids of different types, so one lookup tells them apart.
/// </summary>
internal static class AddressScopes
{
    public static readonly uint Session = TypeId<SessionRef>.Value;
    public static readonly uint Origin = TypeId<Origin>.Value;
    public static readonly uint Group = TypeId<ClientGroup>.Value;
}

internal enum AddressKind : byte
{
    Actor,
    Node,
    Group,
}

/// <summary>
/// How messages to <typeparamref name="TAddr"/> are routed, resolved once per address type: its kind (a JIT constant
/// in the send paths), its scope (the frame's target: the type id, which for an actor id is its actor type) and its
/// fields, read in place without boxing.
/// </summary>
internal static class Addresses<TAddr> where TAddr : struct, IAddress
{
    public static readonly AddressKind Kind = KindOf();

    public static readonly uint Scope = TypeId<TAddr>.Value;

    /// <summary>The actor key, session id or group id.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Value(in TAddr address) =>
        Kind == AddressKind.Node ? Unsafe.As<TAddr, NodeLayout>(ref Unsafe.AsRef(in address)).Value
        : Unsafe.As<TAddr, long>(ref Unsafe.AsRef(in address));

    /// <summary>The node of an <see cref="INodeAddress"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Node(in TAddr address) => Unsafe.As<TAddr, NodeLayout>(ref Unsafe.AsRef(in address)).Node;

    private readonly record struct NodeLayout(ulong Node, long Value);

    /// <summary>
    /// The kind, after checking that the type's layout is what <see cref="Value"/> and <see cref="Node"/> read: a value
    /// built from known fields must answer them through its own interface members (boxed once, here).
    /// </summary>
    private static AddressKind KindOf()
    {
        var probe = new NodeLayout(0x0102030405060708UL, 0x1112131415161718L);
        if (default(TAddr) is IActorId or IGroupAddress)
        {
            long value = probe.Value;
            var address = Unsafe.SizeOf<TAddr>() == sizeof(long) ? Unsafe.As<long, TAddr>(ref value) : default;
            if (address is IActorId actor && actor.Value == value) return AddressKind.Actor;
            if (address is IGroupAddress group && group.Value == value) return AddressKind.Group;
        }
        else if (default(TAddr) is INodeAddress && Unsafe.SizeOf<TAddr>() == Unsafe.SizeOf<NodeLayout>())
        {
            var node = (INodeAddress)Unsafe.As<NodeLayout, TAddr>(ref probe);
            if (node.Node == probe.Node && node.Value == probe.Value) return AddressKind.Node;
        }

        throw new InvalidOperationException(
            $"Address type {typeof(TAddr)} must be an IActorId or IGroupAddress holding exactly one long, or an INodeAddress holding a ulong node then a long value");
    }
}

/// <summary>Where a client should connect: a client-facing node's public host and client port (the directory's answer).</summary>
[Nino.Core.NinoType]
public readonly partial record struct ClientEndpoint(string Host, int Port)
{
    /// <summary>The answer "stay where you are" (the node itself, or nodes behind a load balancer).</summary>
    public static ClientEndpoint Stay => new("", 0);
}
