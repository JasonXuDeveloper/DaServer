using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Buffers;
using Nino.Core;

namespace DaServer.Rpc;

// Message-first: a message is a [NinoType, Message] struct, a handler is a [Handle] method taking it. Nothing else is
// declared. Plain [NinoType] types are data inside messages (fields, replies), never sent on their own.
//
//   Cluster.Send(key, msg)        one-way to the actor of that key handling msg   (Send(msg): the service handling it)
//   Cluster.Ask(key, request)     request/reply: request implements IRequest<TReply>, its handler returns TReply
//   Cluster.Publish(e)            to every handler of e (services); Publish(key, e) also reaches actor handlers of key
//   Cluster.Send(session, msg)    push to one client; Send(group, msg) broadcasts to a client group
//   client.Send(msg) / Ask(..)    clients send Flow.FromClient messages; actor handlers get the key bound to the session

/// <summary>
/// A message answered with a <typeparamref name="TReply"/>: <c>Cluster.Ask(key, new GetInfo())</c>. Its handler
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

/// <summary>Who a message travels between (<see cref="MessageAttribute"/>). Servers can always send it to each other.</summary>
[Flags]
public enum Flow
{
    /// <summary>Between servers only: actors and services on any node (login, game, map, gateway services).</summary>
    Internal = 0,

    /// <summary>
    /// Game clients may send it through a gateway, which adds the client's session (<c>CallerSession</c>). Handled by a
    /// service: any node hosting it. Handled by an actor: the key bound to the session (<c>BindSession</c>), so a
    /// client never reaches another player's actor, unless <see cref="MessageAttribute.AnyKey"/> is set.
    /// </summary>
    FromClient = 1,

    /// <summary>Servers push it to client sessions and groups (<c>Send(session, m)</c>), handled on clients.</summary>
    ToClient = 2,
}

/// <summary>
/// Marks a <c>[NinoType]</c> struct as a message: what is sent, published, pushed or asked (with
/// <see cref="IRequest{TReply}"/>) and taken by <see cref="HandleAttribute">[Handle]</see> methods. Plain
/// <c>[NinoType]</c> types are data inside messages. <see cref="Flow"/> says who may send it to whom; the analyzer
/// rejects a message used against its flow (a server pushing an internal message, a client sending one).
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class MessageAttribute(Flow flow = Flow.Internal) : Attribute
{
    public Flow Flow { get; } = flow;

    /// <summary><see cref="Flow.FromClient"/> to actors only: the client chooses the key (public actors: rooms, leaderboards).</summary>
    public bool AnyKey { get; set; }
}

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
/// Emitted by the generator into every assembly declaring client messages, pointing at one generated type of the
/// assembly. Hosts use it to run the assembly's module initializer (client message registration) at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MessagesAssemblyAttribute(Type anchor) : Attribute
{
    public Type Anchor { get; } = anchor;
}

/// <summary>
/// Sends messages into the cluster: implemented by the server runtime (<c>ICluster</c>) and by game clients
/// (<c>ClusterClient</c>). The generic <c>Ask</c> members are what generated <c>Ask(request)</c> overloads call (they
/// fix <c>TReply</c> from the request type); business code calls those.
/// </summary>
public interface IMessageSender
{
    /// <summary>One-way message to the actor of <paramref name="key"/> handling <typeparamref name="T"/>.</summary>
    void Send<T>(long key, in T message) where T : struct;

    /// <summary>
    /// One-way message to the service handling <typeparamref name="T"/> (from a client: also to the actor bound to the
    /// client's session).
    /// </summary>
    void Send<T>(in T message) where T : struct;

    [EditorBrowsable(EditorBrowsableState.Never)]
    ValueTask<TReply> Ask<TRequest, TReply>(long key, in TRequest request) where TRequest : struct, IRequest<TReply>;

    [EditorBrowsable(EditorBrowsableState.Never)]
    ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply>;
}

/// <summary>
/// The gateway's session of a client closed (disconnect, kick, gateway crash). Delivered to every actor the session
/// was bound to (<c>BindSession</c>) that handles it; for a lost connection, only once the client had
/// <see cref="Runtime.NodeOptions.ReconnectGrace"/> to resume (and it did not).
/// </summary>
[NinoType, Message]
public readonly partial record struct SessionClosed(SessionRef Session);

/// <summary>
/// A client that lost its connection resumed on a gateway: <see cref="Session"/> replaces <see cref="Previous"/> and is
/// already bound to every actor <see cref="Previous"/> was bound to (and in their groups). Delivered to each of them
/// that handles it, as the session of the turn (<c>CallerSession</c>): update the stored session, rejoin other groups
/// and send the client its full state (pushes sent meanwhile were lost). <see cref="SessionClosed"/> for
/// <see cref="Previous"/> is not delivered afterwards.
/// </summary>
[NinoType, Message]
public readonly partial record struct SessionResync(SessionRef Session, SessionRef Previous);

/// <summary>Wire identity of message type <typeparamref name="T"/>: [WireId] or FNV-1a of its full name, computed once.</summary>
public static class MessageType<T> where T : struct
{
    public static readonly uint Id = typeof(T).GetCustomAttribute<WireIdAttribute>()?.Id ??
                                     RpcIds.Fnv1a(typeof(T).FullName ?? typeof(T).Name);
}

/// <summary>
/// Generated dispatcher: deserialize the message from <paramref name="reader"/>, invoke <paramref name="target"/>'s
/// handler and reply through <paramref name="context"/> (requests).
/// </summary>
public delegate ValueTask MessageDispatcher(object target, ref Reader reader, in CallContext context);

/// <summary>One message type handled by a type (generated registration).</summary>
public sealed class MessageHandler(uint messageId, MessageDispatcher dispatcher, Type messageType)
{
    public uint MessageId { get; } = messageId;
    public MessageDispatcher Dispatcher { get; } = dispatcher;
    public Type MessageType { get; } = messageType;

    /// <summary>Handler registration for <typeparamref name="T"/>; <paramref name="dispatcher"/> is generated.</summary>
    public static MessageHandler Of<T>(MessageDispatcher dispatcher) where T : struct =>
        new(MessageType<T>.Id, dispatcher, typeof(T));

    public override string ToString() => MessageType.FullName ?? MessageType.Name;
}

/// <summary>The message handlers of one type, by message id (lock-free lookups).</summary>
public sealed class MessageHandlers
{
    private readonly FrozenDictionary<uint, MessageHandler> _byId;

    public MessageHandlers(MessageHandler[] handlers)
    {
        All = handlers;
        var byId = new Dictionary<uint, MessageHandler>();
        foreach (var handler in handlers)
        {
            if (byId.TryGetValue(handler.MessageId, out var other))
                throw new InvalidOperationException(
                    $"Message id collision 0x{handler.MessageId:x8}: {handler} and {other}. Use [WireId] on one of them.");
            byId[handler.MessageId] = handler;
        }

        _byId = byId.ToFrozenDictionary();
    }

    public MessageHandler[] All { get; }

    /// <summary>Handler of message <paramref name="messageId"/>, or null.</summary>
    public MessageHandler? Of(uint messageId) => _byId.TryGetValue(messageId, out var handler) ? handler : null;
}

/// <summary>A message type game clients may send (<see cref="Flow.FromClient"/>).</summary>
public readonly record struct ClientMessageInfo(uint Id, Type Type, bool AnyKey);

/// <summary>
/// Process-wide registries filled by generated module initializers (no reflection scanning, no configuration): the
/// messages clients may send (read by gateways) and the handlers of client-side handler objects.
/// </summary>
public static class MessageRegistry
{
    private static readonly Lock Gate = new();
    private static FrozenDictionary<uint, ClientMessageInfo> _clientMessages = FrozenDictionary<uint, ClientMessageInfo>.Empty;
    private static FrozenDictionary<Type, MessageHandlers> _handlerTypes = FrozenDictionary<Type, MessageHandlers>.Empty;

    /// <summary>Register a client message type. Idempotent per type; throws on wire-id collisions.</summary>
    public static void RegisterClientMessage<T>(bool anyKey) where T : struct
    {
        var info = new ClientMessageInfo(MessageType<T>.Id, typeof(T), anyKey);
        lock (Gate)
        {
            if (_clientMessages.TryGetValue(info.Id, out var existing))
            {
                if (existing.Type == info.Type) return;
                throw new InvalidOperationException(
                    $"Message id collision 0x{info.Id:x8}: {info.Type} and {existing.Type}. Use [WireId] on one of them.");
            }

            var next = new Dictionary<uint, ClientMessageInfo>(_clientMessages) { [info.Id] = info };
            Volatile.Write(ref _clientMessages, next.ToFrozenDictionary());
        }
    }

    /// <summary>Register the handlers of a client-side handler type (<c>client.Register(handler)</c>).</summary>
    public static void RegisterHandlers(Type type, MessageHandler[] handlers)
    {
        lock (Gate)
        {
            if (_handlerTypes.ContainsKey(type)) return;
            var next = new Dictionary<Type, MessageHandlers>(_handlerTypes) { [type] = new MessageHandlers(handlers) };
            Volatile.Write(ref _handlerTypes, next.ToFrozenDictionary());
        }
    }

    /// <summary>The client message with wire id <paramref name="id"/> (gateways refuse every other message).</summary>
    public static bool TryGetClientMessage(uint id, out ClientMessageInfo info) =>
        Volatile.Read(ref _clientMessages).TryGetValue(id, out info);

    public static MessageHandlers HandlersOf(Type type)
    {
        if (Volatile.Read(ref _handlerTypes).TryGetValue(type, out var handlers)) return handlers;
        RuntimeHelpersEnsure(type);
        return Volatile.Read(ref _handlerTypes).TryGetValue(type, out handlers)
            ? handlers
            : throw new InvalidOperationException($"{type} has no [Handle] methods (is the DaServer generator referenced by its assembly?)");
    }

    private static void RuntimeHelpersEnsure(Type type) =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
}

/// <summary>Serialization of a message into a pooled frame (shared by the server and the client senders).</summary>
internal static class MessageWriter
{
    public static FrameWriter Write<T>(in T message) where T : struct
    {
        var w = FrameWriter.Rent();
        try
        {
            var nino = w.Nino;
            NinoSerializer.Serialize(message, ref nino);
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }
}
