using System.ComponentModel;
using System.Reflection;

namespace DaServer.Rpc;

/// <summary>
/// Generated dispatcher: read the message from <paramref name="payload"/>, invoke
/// <paramref name="target"/>'s handler and reply through <paramref name="context"/> (requests).
/// </summary>
public delegate ValueTask MessageDispatcher(object target, ReadOnlySpan<byte> payload, in CallContext context);

/// <summary>
/// Reads messages for generated dispatchers. A payload that is not the message it names (truncated, written by another
/// build, hostile) is refused before any handler runs: the sender gets <see cref="RpcStatus.Malformed"/>, and the target
/// is not charged with a failure (its actor's <c>OnError</c> does not run, its state was never touched).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MessageReader
{
    public static bool TryRead<T>(ReadOnlySpan<byte> payload, out T message)
    {
        try
        {
            message = NinoDeserializer.Deserialize<T>(payload);
            return true;
        }
        catch (Exception)
        {
            message = default!;
            return false;
        }
    }

    /// <summary>Refuse a request whose payload <see cref="TryRead{T}"/> could not read (one-way messages are dropped).</summary>
    public static ValueTask Refuse(in CallContext context)
    {
        context.ReplyError(RpcStatus.Malformed, null);
        return default;
    }
}

/// <summary>One message type handled by a type (generated registration).</summary>
[EditorBrowsable(EditorBrowsableState.Never)] // generated code calls it
public sealed class MessageHandler(uint messageId, MessageDispatcher dispatcher, Type messageType)
{
    public uint MessageId { get; } = messageId;
    public MessageDispatcher Dispatcher { get; } = dispatcher;
    public Type MessageType { get; } = messageType;

    /// <summary>Handler registration for <typeparamref name="T"/>; <paramref name="dispatcher"/> is generated.</summary>
    public static MessageHandler Of<T>(MessageDispatcher dispatcher) where T : struct =>
        new(TypeId<T>.Value, dispatcher, typeof(T));

    public override string ToString() => MessageType.FullName ?? MessageType.Name;
}

/// <summary>The message handlers of one type, by message id (lock-free lookups).</summary>
[EditorBrowsable(EditorBrowsableState.Never)] // generated code calls it
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
                    $"Message id collision 0x{handler.MessageId:x8}: {handler} and {other}. Give one of them another id with [NinoFormerName].");
            byId[handler.MessageId] = handler;
        }

        _byId = byId.ToFrozenDictionary();
    }

    public MessageHandler[] All { get; }

    /// <summary>Handler of message <paramref name="messageId"/>, or null.</summary>
    public MessageHandler? Of(uint messageId) => _byId.TryGetValue(messageId, out var handler) ? handler : null;
}

/// <summary>
/// The handlers of client-side handler objects (<c>client.Register(handler)</c>), filled by generated module
/// initializers (no reflection scanning, no configuration).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HandlerRegistry
{
    private static readonly Lock Gate = new();
    private static FrozenDictionary<Type, MessageHandlers> _handlerTypes = FrozenDictionary<Type, MessageHandlers>.Empty;

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

    public static MessageHandlers HandlersOf(Type type)
    {
        if (Volatile.Read(ref _handlerTypes).TryGetValue(type, out var handlers)) return handlers;
        Registrations.Run(type.Assembly); // the assembly's generated registration
        return Volatile.Read(ref _handlerTypes).TryGetValue(type, out handlers)
            ? handlers
            : throw new InvalidOperationException($"{type} has no [Handle] methods (is the DaServer generator referenced by its assembly?)");
    }
}

/// <summary>Serialization of a message or a reply into a pooled frame (shared by the server and the client senders).</summary>
internal static class MessageWriter
{
    public static FrameWriter Write<T>(in T message)
    {
        var w = FrameWriter.Rent();
        try
        {
            w.Write(in message);
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }
}
