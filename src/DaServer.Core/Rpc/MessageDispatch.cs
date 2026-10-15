using System.ComponentModel;
using System.Reflection;

namespace DaServer.Rpc;

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
