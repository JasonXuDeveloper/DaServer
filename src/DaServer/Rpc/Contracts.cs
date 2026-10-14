using System;

namespace DaServer.Rpc;

// A contract is an interface with one attribute saying who serves it; [FromClient] says what game clients may call.
// Everything else (proxies, dispatch, wire ids, registration, discovery) is generated.
//
//   [Service]     any node hosting it (load-balanced, local node first)          Cluster.Account().Login(..)
//   [Actor]       the actor of a key (one per cluster, turn-based)               Cluster.Player(id).AddGold(..)
//   [ToClient]    server to client: one session or a client group (clients       Cluster.PlayerClient(session).OnGold(..)
//                 implement it)
//   [FromClient]  client to server, on a [Service]/[Actor] method (or the whole interface): game clients may call it
//                 through a gateway; an actor method reaches the key the server bound to the client's session
//                 (Cluster.Bind); [FromClient(AnyKey = true)] lets the client choose the key (rooms, leaderboards).

/// <summary>
/// Stateless service contract. Methods return <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c> or <c>void</c> (one-way);
/// any node hosting an implementation may serve a call (the local node first).
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class ServiceAttribute : Attribute;

/// <summary>
/// Keyed actor contract (virtual actor): one activation per key in the cluster, placed by rendezvous hashing over the
/// nodes hosting it, activated on demand, one message at a time.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class ActorAttribute : Attribute;

/// <summary>Server-to-client contract, implemented by game clients. All methods return <c>void</c>.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class ToClientAttribute : Attribute;

/// <summary>
/// Game clients may call this method (or every method of the interface) through a gateway, which adds the caller's
/// session. On an actor the call reaches the key bound to the session (<c>Cluster.Bind&lt;IPlayer&gt;(session, id)</c>),
/// so a client can never address another player's actor, unless <see cref="AnyKey"/> is set.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
public sealed class FromClientAttribute : Attribute
{
    /// <summary>Actor methods only: the client chooses the key (public, read-mostly actors: rooms, leaderboards).</summary>
    public bool AnyKey { get; set; }
}

/// <summary>
/// Overrides the wire id of a contract method. Only to resolve a reported hash collision (DAS005) or to keep
/// compatibility after renaming a method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WireIdAttribute(uint id) : Attribute
{
    public uint Id { get; } = id;
}

/// <summary>
/// Emitted by the generator into every assembly that declares contracts, pointing at one generated type of the
/// assembly. Hosts use it to run the assembly's module initializer (contract registration) at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RpcContractsAssemblyAttribute(Type anchor) : Attribute
{
    public Type Anchor { get; } = anchor;
}
