using System.ComponentModel;
using System.Reflection;

namespace DaServer;

// Message-first: a message is a [NinoType, Message] struct, a handler is a [Handle] method taking it. Nothing else is
// declared. Plain [NinoType] types are data inside messages (fields, replies), never sent on their own.
//
//   Cluster.Send(address, msg)    one-way to an address: an actor id (its type names the actor type), a client session
//                                 or a client group ([ToClient] messages)
//   Cluster.Send(msg)             one-way to the service handling msg
//   Cluster.Ask(mapId, request)   request/reply: request implements IRequest<TReply>, its handler returns TReply
//   Cluster.Publish(e)            to every service handling e
//   client.Send(msg) / Ask(..)    clients send [FromClient] messages: to the actor bound to their session, else a service
//   client.Send(roomId, msg)      to an actor that granted the client's session (GrantSession)

/// <summary>
/// A message answered with a <typeparamref name="TReply"/>: <c>Cluster.Ask(playerId, new GetInfo())</c>. Its handler
/// returns <typeparamref name="TReply"/> or <c>ValueTask&lt;TReply&gt;</c>. A marker only (never boxed: the generated
/// <c>Ask</c> overloads take the struct itself).
/// </summary>
public interface IRequest<TReply>;

/// <summary>
/// Marks a cluster message handler on an actor, a service or a client handler object: one parameter (the message,
/// a <c>[NinoType]</c> struct), returns <c>void</c> / <c>ValueTask</c>, or the reply of an <see cref="IRequest{TReply}"/>
/// (<c>TReply</c> / <c>ValueTask&lt;TReply&gt;</c>). Registration and dispatch are generated.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HandleAttribute : Attribute;

/// <summary>
/// Marks a <c>[NinoType]</c> struct as a message: what is sent, published or asked (with
/// <see cref="IRequest{TReply}"/>) and taken by <see cref="HandleAttribute">[Handle]</see> methods. Plain
/// <c>[NinoType]</c> types are data inside messages. A message travels between servers; client connections add who else
/// may send or receive it (<see cref="FromClientAttribute"/>, <see cref="ToClientAttribute"/>), and the
/// analyzer rejects a message used against them (a server sending a server-only message to a client, a client sending
/// one).
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class MessageAttribute : Attribute;

/// <summary>
/// Overrides the wire id of a message type (FNV-1a of its full name by default). Only to resolve a reported collision
/// or to keep compatibility after renaming a message.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class WireIdAttribute(uint id) : Attribute
{
    public uint Id { get; } = id;
}

/// <summary>
/// Emitted by the generator into every assembly it writes registrations for (client messages, handler dispatchers,
/// systems), naming the generated class whose static constructor runs them. A static constructor runs exactly once on
/// every runtime, Unity's IL2CPP included, when <see cref="Registrations.Run"/> triggers it; on .NET the generated
/// class also triggers it from a module initializer when its assembly loads, so servers need no call.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedRegistrationAttribute(Type registration) : Attribute
{
    public Type Registration { get; } = registration;
}

/// <summary>
/// Emitted by the generator into assemblies built with <c>&lt;DaServerShared&gt;true&lt;/DaServerShared&gt;</c>: rules and
/// ECS systems servers and clients both run (no server runtime or transport may be referenced: DAS010). A node project
/// referencing such an assembly runs its systems on every shard, as it runs its own.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SharedLogicAttribute : Attribute;

/// <summary>Runs the generated registrations of an assembly (<see cref="GeneratedRegistrationAttribute"/>).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Registrations
{
    /// <summary>Run every registration of <paramref name="assembly"/> (once each; later calls return at once).</summary>
    public static void Run(Assembly assembly)
    {
        foreach (var registration in assembly.GetCustomAttributes<GeneratedRegistrationAttribute>())
            RuntimeHelpers.RunClassConstructor(registration.Registration.TypeHandle);
    }
}

/// <summary>
/// Sends messages into the cluster: implemented by the server runtime (<c>ICluster</c>) and by game clients
/// (<c>ClusterClient</c>). The generic <c>Ask</c> members are what generated <c>Ask(request)</c> overloads call (they
/// fix <c>TReply</c> from the request type); business code calls those.
/// </summary>
public interface IMessageSender
{
    /// <summary>
    /// One-way message to <paramref name="address"/>: an actor (<c>Cluster.Send(mapId, new Move(..))</c>), a client
    /// session or a client group (servers only, <c>[ToClient]</c> messages).
    /// </summary>
    void Send<TAddr, T>(TAddr address, in T message) where TAddr : struct, IAddress where T : struct;

    /// <summary>
    /// One-way message to the service handling <typeparamref name="T"/> (from a client: also to the actor bound to the
    /// client's session).
    /// </summary>
    void Send<T>(in T message) where T : struct;

    [EditorBrowsable(EditorBrowsableState.Never)]
    ValueTask<TReply> Ask<TAddr, TRequest, TReply>(TAddr address, in TRequest request)
        where TAddr : struct, IAddress where TRequest : struct, IRequest<TReply>;

    [EditorBrowsable(EditorBrowsableState.Never)]
    ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply>;
}

/// <summary>
/// A client's session closed (disconnect, kick, crash of its node). Delivered to every actor the session
/// was bound or granted to (<c>BindSession</c>, <c>GrantSession</c>) that handles it; for a lost connection, only once the client had
/// <see cref="Runtime.NodeOptions.ReconnectGrace"/> to resume (and it did not).
/// </summary>
[NinoType, Message]
public readonly partial record struct SessionClosed(ClientSession Session);

/// <summary>
/// A client that lost its connection resumed on a client-facing node: <see cref="Session"/> replaces <see cref="Previous"/> at
/// every actor that still held <see cref="Previous"/> bound or granted (the runtime checks it; a revoked, closed or
/// already resumed session resumes nothing), and is then bound or granted to it again (and in its group). Delivered to
/// each such actor that handles it, as the session of the turn (<c>Caller</c>): update the stored session, rejoin
/// other groups and send the client its full state (pushes sent meanwhile were lost). <see cref="SessionClosed"/> for
/// <see cref="Previous"/> is not delivered afterwards.
/// </summary>
[NinoType, Message]
public readonly partial record struct SessionResync(ClientSession Session, ClientSession Previous);

// Session operations: messages to a client address that the node holding the session applies instead of delivering
// them to the client. To a session (or the Caller): that session; to a client group: every member, on every node.
// They travel on the same ordered link as the messages to the session, so what follows them sees their effect.

/// <summary>Add the session (or every member of the group) to <see cref="Group"/>: <c>Cluster.Send(Caller, new JoinGroup(cell))</c>.</summary>
[NinoType, Message, ToClient]
public readonly partial record struct JoinGroup(ClientGroup Group);

/// <summary>Remove the session (or every member of the group) from <see cref="Group"/>.</summary>
[NinoType, Message, ToClient]
public readonly partial record struct LeaveGroup(ClientGroup Group);

/// <summary>Close the session (or every session of the group): its actors get <see cref="SessionClosed"/> at once, no reconnect grace.</summary>
[NinoType, Message, ToClient]
public readonly partial record struct Disconnect;

/// <summary>
/// Change what the session's client may address (<see cref="Runtime.Actor.BindSession"/>,
/// <see cref="Runtime.Actor.GrantSession"/>, asked so the actor resumes once it holds) for actor
/// (<see cref="HandlerType"/>, <see cref="Key"/>).
/// </summary>
[NinoType, Message]
internal readonly partial record struct SessionAccess(uint HandlerType, long Key, AccessChange Change);

/// <summary>What a <see cref="SessionAccess"/> changes.</summary>
internal enum AccessChange : byte
{
    /// <summary>Grant the actor and make it the session's actor of its type (the target of messages naming no actor).</summary>
    Bind,

    /// <summary>The session has no actor of the type any more (its grant goes too).</summary>
    Unbind,

    /// <summary>The client may name the actor (<c>client.Send(id, ..)</c>).</summary>
    Grant,

    /// <summary>The client may not name the actor any more (a binding to it goes too).</summary>
    Revoke,
}

/// <summary>Wire identity of message type <typeparamref name="T"/>: [WireId] or FNV-1a of its full name, computed once.</summary>
public static class MessageType<T> where T : struct
{
    public static readonly uint Id = typeof(T).GetCustomAttribute<WireIdAttribute>()?.Id ??
                                     WireIds.Fnv1a(typeof(T).FullName ?? typeof(T).Name);
}
