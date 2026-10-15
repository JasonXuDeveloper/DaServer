namespace DaServer.Runtime;

/// <summary>A hosted actor or service handler type and the messages it handles (generated registration).</summary>
public sealed class HandlerTypeInfo(Type type, bool isActor, Func<object> factory, MessageHandler[] handlers)
{
    public Type Type { get; } = type;

    /// <summary>Keyed actor (one loaded actor per key in the cluster) rather than a stateless service.</summary>
    public bool IsActor { get; } = isActor;

    public Func<object> Factory { get; } = factory;
    public MessageHandlers Handlers { get; } = new(handlers);

    /// <summary>Identity of the handler type in placement, membership and routing (FNV-1a of the type name).</summary>
    public uint Id { get; } = RpcIds.Fnv1a(type.FullName ?? type.Name);

    public override string ToString() => Type.FullName ?? Type.Name;
}

/// <summary>An ECS system added to every shard of the nodes hosting it (generated registration).</summary>
public sealed class SystemInfo(Type type, Func<EcsSystem> factory)
{
    public Type Type { get; } = type;
    public Func<EcsSystem> Factory { get; } = factory;
}

/// <summary>
/// Process-wide registry of server-side handler types and systems, filled by generated module initializers.
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
                if (existing.Type == info.Type) return;
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

    public static HandlerTypeInfo[] HandlerTypes
    {
        get { lock (Gate) return HandlerTypeList.ToArray(); }
    }

    public static SystemInfo[] Systems
    {
        get { lock (Gate) return SystemList.ToArray(); }
    }
}
