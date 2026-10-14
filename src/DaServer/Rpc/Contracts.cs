using System;

namespace DaServer.Rpc;

/// <summary>
/// Marker for a stateless RPC service contract. Any node hosting an implementation may serve a call;
/// callers are load-balanced (local node preferred). Methods must return <c>ValueTask</c>,
/// <c>ValueTask&lt;T&gt;</c> or <c>void</c> (fire-and-forget).
/// </summary>
public interface IService
{
}

/// <summary>
/// Marker for a stateful, keyed RPC actor contract (virtual actor). Exactly one activation per key exists in the
/// cluster; it is placed by rendezvous hashing over the nodes hosting the actor type and activated on demand.
/// Calls to one activation are processed one at a time on its shard thread.
/// </summary>
public interface IActor
{
}

/// <summary>
/// Marker for a server-to-client push contract, implemented on the client. All methods must return <c>void</c>.
/// </summary>
public interface IClientCallback
{
}

/// <summary>
/// Allows game clients (through a gateway) to call this <see cref="IService"/> / <see cref="IActor"/> contract.
/// For actors the key is taken from the session binding, never from the client, unless
/// <see cref="AnyKey"/> is set.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class ClientAccessibleAttribute : Attribute
{
    /// <summary>
    /// Let the client choose the actor key (e.g. public read-only actors like rooms or leaderboards).
    /// </summary>
    public bool AnyKey { get; set; }
}

/// <summary>
/// Overrides the wire id of a contract method. Use only to resolve a reported hash collision or to keep
/// compatibility after renaming a method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcIdAttribute : Attribute
{
    public RpcIdAttribute(uint id) => Id = id;
    public uint Id { get; }
}

/// <summary>
/// Emitted by the generator into every assembly that declares RPC contracts, pointing at one generated type of the
/// assembly. Hosts use it to run the assembly's module initializer (contract registration) at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RpcContractsAssemblyAttribute(Type anchor) : Attribute
{
    public Type Anchor { get; } = anchor;
}
