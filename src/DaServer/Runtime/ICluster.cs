namespace DaServer.Runtime;

/// <summary>
/// What business code sees of the cluster (<c>Cluster</c> in actors and services, <c>world.Cluster()</c> in systems):
/// messages to actors and services (<c>Cluster.Send(mapId, new Move(..))</c>, <c>await Cluster.Ask(playerId, new
/// GetInfo())</c>), events, pushes to clients and client groups. Node lifecycle and internals are not part of it.
/// </summary>
public interface ICluster : IMessageSender
{
    /// <summary>Id of the local node.</summary>
    ulong NodeId { get; }

    /// <summary>
    /// Publish an event (a <c>[NinoType]</c> struct) to every service handling it (<see cref="HandleAttribute">[Handle]</see>):
    /// serialized once, one frame per handling implementation. The publisher never depends on its handlers.
    /// </summary>
    void Publish<TEvent>(in TEvent e) where TEvent : struct;

    /// <summary>Publish an event to every service handling it and to every actor of <paramref name="key"/> handling it.</summary>
    void Publish<TEvent>(long key, in TEvent e) where TEvent : struct;

    /// <summary>Push a message to one client session (its client handles it with a [Handle] method).</summary>
    void Send<T>(SessionRef session, in T message) where T : struct;

    /// <summary>Broadcast a message to every session of a group on every gateway (serialized once).</summary>
    void Send<T>(ClientGroup group, in T message) where T : struct;

    /// <summary>Add a session to a broadcast group (an actor's own <c>Group</c> is joined by <c>BindSession</c>).</summary>
    void JoinGroup(SessionRef session, ClientGroup group);

    /// <summary>Remove a session from a broadcast group.</summary>
    void LeaveGroup(SessionRef session, ClientGroup group);

    /// <summary>Disconnect a client session.</summary>
    void Disconnect(SessionRef session);
}
