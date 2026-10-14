using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DaServer.Rpc;
using EasyEcs;

namespace DaServer.Runtime;

/// <summary>A hosted implementation of one or more contracts (generated registration).</summary>
public sealed class ImplementationInfo(Type type, RpcContractKind kind, Func<object> factory, Type[] contracts, string[] roles)
{
    public Type Type { get; } = type;
    public RpcContractKind Kind { get; } = kind;
    public Func<object> Factory { get; } = factory;
    public Type[] Contracts { get; } = contracts;

    /// <summary>Node roles that host this implementation; empty = every node.</summary>
    public string[] Roles { get; } = roles;

    /// <summary>Placement identity of actor implementations (FNV-1a of the type name).</summary>
    public uint Id { get; } = RpcIds.Fnv1a(type.FullName ?? type.Name);

    public override string ToString() => Type.FullName ?? Type.Name;
}

/// <summary>An ECS system added to every shard of nodes with a matching role (generated registration).</summary>
public sealed class SystemInfo(Type type, Func<EcsSystem> factory, string[] roles)
{
    public Type Type { get; } = type;
    public Func<EcsSystem> Factory { get; } = factory;
    public string[] Roles { get; } = roles;
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

    internal static bool IsHostedBy(string[] roles, IReadOnlyCollection<string> nodeRoles)
    {
        if (roles.Length == 0) return true;
        foreach (var role in roles)
            foreach (var nodeRole in nodeRoles)
                if (string.Equals(role, nodeRole, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }
}

/// <summary>Restrict an actor/service implementation or an ECS system to nodes with one of these roles.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RoleAttribute(params string[] roles) : Attribute
{
    public string[] Roles { get; } = roles;
}
