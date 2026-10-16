namespace DaServer.Runtime;

/// <summary>A hosted actor or service handler type and the messages it handles (generated registration).</summary>
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

    private static uint Identity(Type type) => WireIds.Fnv1a(type.FullName ?? type.Name); // == TypeId<T>.Value

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
public sealed class SystemInfo(Type type, Func<EcsSystem> factory)
{
    public Type Type { get; } = type;
    public Func<EcsSystem> Factory { get; } = factory;
}

/// <summary>
/// Process-wide registry of server-side handler types and systems, filled by the generated registrations (ADR-012).
/// Read once at node start; not on any hot path.
/// </summary>
public static class ServerRegistry
{
    private static readonly object Gate = new();
    private static readonly List<HandlerTypeInfo> HandlerTypeList = new();
    private static readonly List<SystemInfo> SystemList = new();

    public static void RegisterHandlerType(HandlerTypeInfo info)
    {
        lock (Gate)
        {
            foreach (var existing in HandlerTypeList)
            {
                if (existing.Type == info.Type) return;
                if (existing.Id == info.Id)
                    throw new InvalidOperationException(existing.IdType == info.IdType && info.IdType != null
                        ? $"{existing} and {info} share the id type {info.IdType}: each actor type needs its own"
                        : $"{existing} and {info} hash to the same id");
            }

            HandlerTypeList.Add(info);
        }
    }

    public static void RegisterSystem(SystemInfo info)
    {
        lock (Gate)
        {
            foreach (var existing in SystemList)
                if (existing.Type == info.Type) return;
            SystemList.Add(info);
        }
    }

    private static readonly List<Persistence.PersistentType> PersistentList = new();

    /// <summary>A persisted component type (<see cref="PersistAttribute"/>) of a components project the node references.</summary>
    public static void RegisterPersistent<T>(uint wireId) where T : struct, IComponent
    {
        lock (Gate)
        {
            foreach (var existing in PersistentList)
            {
                if (existing.Type == typeof(T)) return;
                if (existing.WireId == wireId)
                    throw new InvalidOperationException($"Persisted components {existing.Type} and {typeof(T)} hash to the same wire id: add [WireId] to one");
            }

            PersistentList.Add(new Persistence.PersistentType<T>(wireId));
        }
    }

    internal static Persistence.PersistentType[] PersistentTypes
    {
        get { lock (Gate) return PersistentList.ToArray(); }
    }

    public static HandlerTypeInfo[] HandlerTypes
    {
        get { lock (Gate) return HandlerTypeList.ToArray(); }
    }

    public static SystemInfo[] Systems
    {
        get { lock (Gate) return SystemList.ToArray(); }
    }
}
