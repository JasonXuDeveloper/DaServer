using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DaServer.Rpc;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>A hosted actor or service implementation and the messages it handles (generated registration).</summary>
public sealed class ImplementationInfo(Type type, bool isActor, Func<object> factory, MessageHandler[] handlers)
{
    public Type Type { get; } = type;

    /// <summary>Keyed actor (one activation per key in the cluster) rather than a stateless service.</summary>
    public bool IsActor { get; } = isActor;

    public Func<object> Factory { get; } = factory;
    public MessageHandlers Handlers { get; } = new(handlers);

    /// <summary>Identity of the implementation in placement, membership and routing (FNV-1a of the type name).</summary>
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
/// Process-wide registry of server-side implementations and systems, filled by generated module initializers.
/// Read once at node start; not on any hot path.
/// </summary>
public static class ServerRegistry
{
    private static readonly object Gate = new();
    private static readonly List<ImplementationInfo> ImplementationList = new();
    private static readonly List<SystemInfo> SystemList = new();

    public static void RegisterImplementation(ImplementationInfo info)
    {
        lock (Gate)
        {
            foreach (var existing in ImplementationList)
                if (existing.Type == info.Type) return;
            ImplementationList.Add(info);
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

    public static ImplementationInfo[] Implementations
    {
        get { lock (Gate) return ImplementationList.ToArray(); }
    }

    public static SystemInfo[] Systems
    {
        get { lock (Gate) return SystemList.ToArray(); }
    }

    /// <summary>Make sure the generated module initializer of <paramref name="type"/>'s assembly has run.</summary>
    public static void EnsureLoaded(Type type) => RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
}
