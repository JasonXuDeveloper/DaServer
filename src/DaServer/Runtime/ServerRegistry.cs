namespace DaServer.Runtime;

/// <summary>An actor or service type and the messages it handles (generated registration, ADR-081).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)] // generated code calls it
public sealed class HandlerTypeInfo(Type type, Type? idType, ActorSettings settings, ActorHandler[] handlers)
{
    /// <summary>The static class of the handlers.</summary>
    public Type Type { get; } = type;

    /// <summary>Keyed actor (one loaded actor per key in the cluster) rather than a stateless service.</summary>
    public bool IsActor => IdType != null;

    /// <summary>The id type of an actor (<c>MapId</c> of <c>[Actor&lt;MapId&gt;]</c>); null for a service.</summary>
    public Type? IdType { get; } = idType;

    /// <summary>The actor type's settings (the constants of its <see cref="ActorAttribute{TId}"/>).</summary>
    public ActorSettings Settings { get; } = settings;

    public ActorHandlers Handlers { get; } = new(handlers);

    /// <summary>
    /// Identity of the handler type in placement, membership and routing: <c>TypeId&lt;TId&gt;</c> of an actor's id type
    /// (what senders name), <c>TypeId&lt;T&gt;</c> of a service type.
    /// </summary>
    public uint Id { get; } = (uint)NinoTypeId.Of(idType ?? type); // == TypeId<T>.Value

    /// <summary>Actors of this type stay where they are loaded (<see cref="StickyAttribute"/>, ADR-079).</summary>
    public bool Sticky { get; } = idType != null && type.IsDefined(typeof(StickyAttribute), inherit: false);

    public override string ToString() => Type.FullName ?? Type.Name;
}

/// <summary>An actor type's settings, as constants of its attribute (data, not virtual members).</summary>
public readonly record struct ActorSettings(int UnloadAfterIdleSeconds, int SaveEverySeconds, int MailboxCapacity, ErrorAction OnError);

/// <summary>The generated dispatch of one handler: read the message from the payload, resolve the data, call, answer.</summary>
public delegate void HandlerDispatch(in Context context, ReadOnlySpan<byte> payload);

/// <summary>One message type handled by an actor or service type (generated registration).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ActorHandler(uint messageId, HandlerDispatch dispatch, Type messageType)
{
    public uint MessageId { get; } = messageId;
    public HandlerDispatch Dispatch { get; } = dispatch;
    public Type MessageType { get; } = messageType;

    public static ActorHandler Of<T>(HandlerDispatch dispatch) where T : struct => new(TypeId<T>.Value, dispatch, typeof(T));

    public override string ToString() => MessageType.FullName ?? MessageType.Name;
}

/// <summary>The handlers of one actor or service type, by message id (lock-free lookups).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ActorHandlers
{
    private readonly FrozenDictionary<uint, ActorHandler> _byId;

    public ActorHandlers(ActorHandler[] handlers)
    {
        All = handlers;
        var byId = new Dictionary<uint, ActorHandler>();
        foreach (var handler in handlers)
        {
            if (byId.TryGetValue(handler.MessageId, out var other))
                throw new InvalidOperationException(
                    $"Message id collision 0x{handler.MessageId:x8}: {handler} and {other}. Give one of them another id with [NinoFormerName].");
            byId[handler.MessageId] = handler;
        }

        _byId = byId.ToFrozenDictionary();
    }

    public ActorHandler[] All { get; }

    /// <summary>Handler of message <paramref name="messageId"/>, or null.</summary>
    public ActorHandler? Of(uint messageId) => _byId.TryGetValue(messageId, out var handler) ? handler : null;
}

/// <summary>What generated dispatch calls besides the handler (answers, refusals of unreadable mail).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class HandlerCall
{
    /// <summary>Reply to the current request now.</summary>
    public static void Reply<T>(in Context context, in T value) => context.Call.ReplyValue(in value);

    /// <summary>Reply to the current request now, unless the handler gives its reply later.</summary>
    public static void Answer<T>(in Context context, in Answer<T> answer)
    {
        if (!answer.IsLater) context.Call.ReplyValue(answer.Value);
    }

    /// <summary>A payload that is not its message: refused (requests get <see cref="RpcStatus.Malformed"/>), the actor untouched.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Malformed(in Context context) => context.Call.ReplyError(RpcStatus.Malformed, null);

    /// <summary>Read an <see cref="Answered{TRequest,TReply}"/>.</summary>
    public static bool TryRead<TRequest, TReply>(ReadOnlySpan<byte> payload, out Answered<TRequest, TReply> answered)
        where TRequest : struct, IRequest<TReply> => Answers.TryRead(payload, out answered);
}

/// <summary>An ECS system added to every shard of the nodes hosting it (generated registration).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)] // generated code calls it
public sealed class SystemInfo(Type type, Func<EcsSystem> factory)
{
    public Type Type { get; } = type;
    public Func<EcsSystem> Factory { get; } = factory;
}

/// <summary>
/// Process-wide registry of server-side handler types and systems, filled by the generated registrations (ADR-012).
/// Read once at node start; not on any hot path.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)] // generated code calls it
public static class ServerRegistry
{
    private static readonly object Gate = new();
    private static readonly List<HandlerTypeInfo> HandlerTypeList = new();
    private static readonly List<SystemInfo> SystemList = new();

    private static readonly List<Persistence.PersistentType> PersistentList = new();

    public static void RegisterHandlerType(HandlerTypeInfo info) => Add(HandlerTypeList, info, static i => i.Type, existing =>
        existing.Id != info.Id ? null
        : existing.IdType == info.IdType && info.IdType != null ? $"{existing} and {info} share the id type {info.IdType}: each actor type needs its own"
        : $"{existing} and {info} hash to the same id");

    public static void RegisterSystem(SystemInfo info) => Add(SystemList, info, static i => i.Type, static _ => null);

    /// <summary>
    /// A component type of a components project the node references that is part of actor records: stored and moved
    /// (<see cref="PersistAttribute"/>, <paramref name="stored"/>) or only moved (<see cref="HandoffAttribute"/>).
    /// </summary>
    /// <summary>A [Persist] component (change-tracked: the generator declares it so).</summary>
    public static void RegisterStored<T>() where T : struct, IComponent, ITrackChanges => RegisterPersistent(new Persistence.PersistentType<T>(
        TypeId<T>.Value, static (world, since) => world.SetAll(new Persistence.Unsaved(), Persistence.Written<T>.Filter.Since(since))));

    /// <summary>A [Handoff] component.</summary>
    public static void RegisterHandoff<T>() where T : struct, IComponent => RegisterPersistent(new Persistence.PersistentType<T>(TypeId<T>.Value, null));

    private static void RegisterPersistent<T>(Persistence.PersistentType<T> type) where T : struct, IComponent =>
        Add(PersistentList, type, static p => p.Type, static existing =>
            existing.TypeId != TypeId<T>.Value ? null
            : $"Persisted components {existing.Type} and {typeof(T)} hash to the same type id: give one another id with [NinoFormerName]");

    internal static Persistence.PersistentType[] PersistentTypes => Snapshot(PersistentList);

    internal static HandlerTypeInfo[] HandlerTypes => Snapshot(HandlerTypeList);

    internal static SystemInfo[] Systems => Snapshot(SystemList);

    /// <summary>
    /// Add <paramref name="item"/> unless its type is registered (registrations may run more than once); a registered
    /// item that <paramref name="clash"/> names a message for (the same id) fails the registration.
    /// </summary>
    private static void Add<T>(List<T> list, T item, Func<T, Type> typeOf, Func<T, string?> clash)
    {
        lock (Gate)
        {
            foreach (var existing in list)
            {
                if (typeOf(existing) == typeOf(item)) return;
                if (clash(existing) is { } message) throw new InvalidOperationException(message);
            }

            list.Add(item);
        }
    }

    private static T[] Snapshot<T>(List<T> list)
    {
        lock (Gate) return list.ToArray();
    }
}
