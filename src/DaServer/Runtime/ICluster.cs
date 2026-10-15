namespace DaServer.Runtime;

/// <summary>
/// What business code sees of the cluster (<c>Cluster</c> in actors and services, <c>world.Cluster()</c> in systems):
/// messages to actors, services, client sessions and client groups (<c>Cluster.Send(mapId, new Move(..))</c>,
/// <c>await Cluster.Ask(playerId, new GetInfo())</c>, <c>Cluster.Send(session, new GoldChanged(..))</c>) and published
/// messages. Node lifecycle and internals are not part of it.
/// </summary>
public interface ICluster : IMessageSender
{
    /// <summary>Id of the local node.</summary>
    ulong NodeId { get; }

    /// <summary>
    /// Publish a message (a <c>[NinoType, Message]</c> struct) to every service handling it (<see cref="HandleAttribute">[Handle]</see>):
    /// serialized once, one frame per handler type. The publisher never depends on its handlers.
    /// </summary>
    void Publish<T>(in T e) where T : struct;

    /// <summary>Add a session to a broadcast group (an actor's own <c>Group</c> is joined by <c>BindSession</c>).</summary>
    void JoinGroup(SessionRef session, ClientGroup group);

    /// <summary>Remove a session from a broadcast group.</summary>
    void LeaveGroup(SessionRef session, ClientGroup group);

    /// <summary>Disconnect a client session.</summary>
    void Disconnect(SessionRef session);
}
