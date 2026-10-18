namespace DaServer.Runtime;

/// <summary>A hosted actor or service handler type and the messages it handles (generated registration).</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)] // generated code calls it
public sealed class HandlerTypeInfo(Type type, bool isActor, Func<object> factory, MessageHandler[] handlers)
{
    public Type Type { get; } = type;

    /// <summary>Keyed actor (one loaded actor per key in the cluster) rather than a stateless service.</summary>
    public bool IsActor { get; } = isActor;

    public Func<object> Factory { get; } = factory;
    public MessageHandlers Handlers { get; } = new(handlers);

    /// <summary>
    /// The id type of an actor (<c>MapId</c> of <c>Map : Actor&lt;MapId, ..&gt;</c>); null for a service.
    /// </summary>
    public Type? IdType { get; } = isActor ? IdTypeOf(type) : null;

    /// <summary>
    /// Identity of the handler type in placement, membership and routing: <c>TypeId&lt;TId&gt;</c> of an actor's id type
    /// (what senders name), <c>TypeId&lt;T&gt;</c> of a service type.
    /// </summary>
    public uint Id { get; } = Identity(isActor ? IdTypeOf(type) : type);

    /// <summary>Actors of this type stay where they are loaded (<see cref="StickyAttribute"/>, ADR-079).</summary>
    public bool Sticky { get; } = isActor && type.IsDefined(typeof(StickyAttribute), inherit: false);

    private static uint Identity(Type type) => (uint)NinoTypeId.Of(type); // == TypeId<T>.Value

    private static Type IdTypeOf(Type actor)
    {
        for (var t = actor; t != null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Actor<>))
                return t.GetGenericArguments()[0];
        throw new InvalidOperationException($"{actor} must derive from Actor<TId> or Actor<TId, TState>");
    }

    public override string ToString() => Type.FullName ?? Type.Name;
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
    public static void RegisterPersistent<T>(bool stored) where T : struct, IComponent =>
        Add(PersistentList, new Persistence.PersistentType<T>(TypeId<T>.Value, stored), static p => p.Type, static existing =>
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
