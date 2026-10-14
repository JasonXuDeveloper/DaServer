using DaServer.Rpc;

namespace DaServer.Runtime;

/// <summary>
/// What business code sees of the cluster (<c>Cluster</c> in actors and services, <c>world.Cluster()</c> in systems):
/// generated proxies hang off it (<c>Cluster.Player(id).AddGold(10)</c>, <c>Cluster.PlayerClient(Group).OnMoved(..)</c>),
/// plus session binding, client groups and events. Node lifecycle and internals are not part of it.
/// </summary>
public interface ICluster : IRpcInvoker
{
    /// <summary>Id of the local node.</summary>
    ulong NodeId { get; }

    /// <summary>
    /// Bind a client session to actor <typeparamref name="TActor"/> with <paramref name="key"/>: the client's calls to that
    /// contract go to this key (the client cannot choose it), and the actor receives
    /// <see cref="SessionClosed"/> when the session ends. A session has at most one key per contract.
    /// </summary>
    void Bind<TActor>(SessionRef session, long key) where TActor : class;

    /// <summary>Remove the binding of <typeparamref name="TActor"/> from a session.</summary>
    void Unbind<TActor>(SessionRef session) where TActor : class;

    /// <summary>Add a session to a broadcast group (usually an actor's <c>Group</c>).</summary>
    void JoinGroup(SessionRef session, ClientGroup group);

    /// <summary>Remove a session from a broadcast group.</summary>
    void LeaveGroup(SessionRef session, ClientGroup group);

    /// <summary>Disconnect a client session.</summary>
    void Kick(SessionRef session);

    /// <summary>
    /// Publish a cluster event (a <c>[NinoType]</c> struct) to every <see cref="HandleAttribute">[Handle]</see> method in
    /// the cluster: serialized once, one frame per handling implementation. The publisher never depends on its handlers.
    /// </summary>
    void Publish<TEvent>(in TEvent e) where TEvent : struct;
}
