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

    /// <summary>
    /// Send <paramref name="message"/> to <paramref name="address"/> once <paramref name="delay"/> has passed (timers as
    /// data: the message is serialized now and held by this shard, then sent as if sent then, so it follows a moved
    /// actor and reaches the actor's mailbox as ordinary mail). Allocates nothing. Call it on a shard thread (actors,
    /// services, systems); a delay instead of <c>await Task.Delay</c>, which would hold the actor's turn.
    /// </summary>
    /// <returns>A handle for <see cref="CancelSend"/>.</returns>
    long SendAfter<TAddr, T>(TimeSpan delay, TAddr address, in T message) where TAddr : struct, IAddress where T : struct;

    /// <summary>Send <paramref name="message"/> to the one service handling it once <paramref name="delay"/> has passed.</summary>
    long SendAfter<T>(TimeSpan delay, in T message) where T : struct;

    /// <summary>
    /// Cancel a delayed send not sent yet, on the shard that scheduled it; false when it was sent, cancelled, or
    /// scheduled by another shard.
    /// </summary>
    bool CancelSend(long timer);
}
