using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DaServer.Rpc;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>
/// A hosted actor or service implementation (generated registration): the contracts it serves and the cluster events
/// it handles.
/// </summary>
public sealed class ImplementationInfo(Type type, RpcContractKind kind, Func<object> factory, Type[] contracts,
    EventHandlerInfo[] events)
{
    public Type Type { get; } = type;
    public RpcContractKind Kind { get; } = kind;
    public Func<object> Factory { get; } = factory;
    public Type[] Contracts { get; } = contracts;
    public EventHandlerInfo[] Events { get; } = events;

    /// <summary>Identity of the implementation in placement, membership and event routing (FNV-1a of the type name).</summary>
    public uint Id { get; } = RpcIds.Fnv1a(type.FullName ?? type.Name);

    /// <summary>Handler of event <paramref name="eventId"/>, or null.</summary>
    public EventHandlerInfo? HandlerOf(uint eventId)
    {
        foreach (var handler in Events)
            if (handler.EventId == eventId) return handler;
        return null;
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

/// <summary>Contract id of <typeparamref name="TContract"/>, resolved once (registration forced on first use).</summary>
internal static class ContractOf<TContract>
{
    public static readonly uint Id = Resolve();

    private static uint Resolve()
    {
        ServerRegistry.EnsureLoaded(typeof(TContract));
        return RpcRegistry.GetContract(typeof(TContract)).Id;
    }
}
