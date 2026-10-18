namespace DaServer.Runtime;

/// <summary>
/// What business code (actors, services) can reach: the shard's ECS world and the cluster. Everything here is used
/// from the shard thread only. The shard itself (its thread, dispatch queue, callbacks) is not exposed: code that ran outside a
/// turn could interleave with an actor's awaiting turn. Time-based logic is ECS: a component with a due tick and a
/// system comparing it to <see cref="ShardWorldExtensions.Tick"/>.
/// </summary>
public abstract class ShardObject
{
    /// <summary>The shard this object lives in.</summary>
    internal Shard Shard { get; set; } = null!;

    /// <summary>The shard's ECS world (shared by every actor, service and system of the shard).</summary>
    public World World => Shard.World;

    /// <summary>
    /// The cluster: messages (<c>Cluster.Send(mapId, new Move(..))</c>, <c>await Cluster.Ask(id, new GetInfo())</c>),
    /// events, pushes to clients and client groups.
    /// </summary>
    public ICluster Cluster => Shard.Node;

    protected ILogger Logger => Shard.Node.Logger;

    /// <summary>The shards' clock (<see cref="NodeOptions.TimeProvider"/>): what tests move on with a manual clock.</summary>
    protected TimeProvider Time => Shard.Node.Options.TimeProvider;

    /// <summary>
    /// Who sent the current message from outside the cluster: a client session (<c>Cluster.Send(Caller, m)</c>,
    /// <c>BindSession(Caller)</c>, <c>GrantSession(Caller)</c>) or an HTTP request of the HTTP plane; invalid for
    /// messages sent inside the cluster. In an actor it holds for the whole turn,
    /// awaits included; in a service (whose calls may overlap) until the first <c>await</c>.
    /// </summary>
    protected Origin Caller => this is Actor actor ? actor.TurnCaller : CallContext.Current.Origin;

    /// <summary>
    /// How long the caller of the current request still waits (its call timeout or <c>CallOptions</c>, minus the time the
    /// request travelled and queued): pass it on to the calls this handler makes (<c>new CallOptions(TimeLeft)</c>), or
    /// skip work nobody will wait for. Infinite for one-way messages. Held like <see cref="Caller"/>: in an actor for the
    /// whole turn, in a service until the first <c>await</c>.
    /// </summary>
    protected TimeSpan TimeLeft
    {
        get
        {
            long deadline = this is Actor actor ? actor.TurnDeadline : CallContext.Current.Deadline;
            return deadline == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(Math.Max(0, deadline - Platform.Milliseconds));
        }
    }

    /// <summary>
    /// Reply <paramref name="reply"/> once <paramref name="done"/> completes (its failure is replied instead), and end
    /// the turn now: an actor takes its next message while an access change (<c>BindSession</c>) or a save it waits for
    /// is pending, so a busy actor's throughput does not hang on round trips. Only as the returned expression of a
    /// handler that is not <c>async</c>, after the handler's last use of its state (DAS011): <c>return
    /// ReplyWhen(BindSession(m.Session), true);</c>.
    /// </summary>
    protected static Reply<T> ReplyWhen<T>(Reply done, T reply)
    {
        DeferredAfter<Done, T>.Set(new Done(done.AsValueTask()), reply);
        return default;
    }

    /// <inheritdoc cref="ReplyWhen{T}(Reply, T)"/>
    protected static Reply<T> ReplyWhen<TDone, T>(Reply<TDone> done, T reply)
    {
        DeferredAfter<Done<TDone>, T>.Set(new Done<TDone>(done.AsValueTask()), reply);
        return default;
    }
}

/// <summary>
/// Base class of stateless services: messages arrive at its [Handle] methods (<c>Cluster.Send(msg)</c>,
/// <c>Cluster.Ask(request)</c>) on any node hosting it (the local node first). One instance per shard; a message runs
/// on the shard that received it, so services may cache per-shard data without locks.
/// </summary>
public abstract class Service : ShardObject;
