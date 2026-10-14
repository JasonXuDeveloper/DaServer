using System;

namespace DaServer.Rpc;

// Contract markers. A contract is an interface deriving from exactly one of them; the marker says who may call it and
// how a target is chosen. Everything else (proxies, dispatch, wire ids, registration) is generated.
//
//   marker            callers             target
//   IService          servers             any node hosting it (load-balanced, local first)
//   IPublicService    servers + clients   same
//   IActor            servers             the activation of a key (placed by rendezvous hashing)
//   ISessionActor     servers + clients   clients reach the key bound to their session (ICluster.Bind)
//   IPublicActor      servers + clients   clients choose the key (public rooms, leaderboards)
//   IClientCallback   servers             one client session or a client group (pushes)

/// <summary>Stateless service called by servers. Methods return <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c> or <c>void</c> (one-way).</summary>
public interface IService;

/// <summary>Stateless service that game clients may call too (through a gateway, which adds the caller session).</summary>
public interface IPublicService : IService;

/// <summary>
/// Stateful keyed actor (virtual actor): one activation per key in the cluster, placed by rendezvous hashing over the
/// nodes hosting it, activated on demand, one message at a time.
/// </summary>
public interface IActor;

/// <summary>
/// Actor that game clients call too, always on the key the server bound to their session
/// (<c>Cluster.Bind&lt;IPlayer&gt;(session, playerId)</c>): a client can never address another player's actor.
/// </summary>
public interface ISessionActor : IActor;

/// <summary>Actor that game clients call with a key of their choice (public, read-mostly actors: rooms, leaderboards).</summary>
public interface IPublicActor : IActor;

/// <summary>Server-to-client push contract, implemented by the game client. All methods return <c>void</c>.</summary>
public interface IClientCallback;

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
