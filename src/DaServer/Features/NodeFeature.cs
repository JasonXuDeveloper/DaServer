namespace DaServer.Features;

/// <summary>
/// A capability a node runs next to its actors and services (client connections, an HTTP edge, ...): what every
/// backend of some kind needs but the runtime does not (ADR-045, ADR-048). Off unless configured: a node runs the
/// features in <see cref="NodeOptions.Features"/>. The runtime never depends on one; a feature reaches the node only
/// through these hooks and <see cref="INodeContext"/>.
/// </summary>
/// <remarks>
/// Order: <see cref="Start"/> (IO side: address handlers, listeners) before the shards start, <see cref="ConfigureShard"/>
/// on each shard thread as its world is built, then the node joins the cluster. On a graceful stop
/// <see cref="DrainAsync"/> runs while the node is leaving and the shards still run, <see cref="Stop"/> after the shards
/// stopped. A crash simulation calls only <see cref="Stop"/>(crashed: true).
/// </remarks>
public abstract class NodeFeature
{
    /// <summary>Register address handlers and start listeners (IO side, before the shards run).</summary>
    protected internal virtual void Start(INodeContext node)
    {
    }

    /// <summary>Add singletons and systems to a shard's world (on that shard's thread, once per shard).</summary>
    protected internal virtual void ConfigureShard(World world)
    {
    }

    /// <summary>The membership view changed (any thread; keep it short and never block).</summary>
    protected internal virtual void OnViewChanged(ClusterView view)
    {
    }

    /// <summary>A member was declared dead (once per member).</summary>
    protected internal virtual void OnMemberDead(ClusterMember member)
    {
    }

    /// <summary>The node is leaving: hand work over (move clients elsewhere, stop accepting) before the shards stop.</summary>
    protected internal virtual ValueTask DrainAsync(CancellationToken cancellationToken) => default;

    /// <summary>The node stopped (<paramref name="crashed"/>: without leaving or draining). Close what is left.</summary>
    protected internal virtual void Stop(bool crashed)
    {
    }

    /// <summary>Whether this feature lets the node take traffic now (readiness, together with the node's own state).</summary>
    public virtual bool IsReady => true;
}

/// <summary>What a <see cref="NodeFeature"/> sees of its node.</summary>
public interface INodeContext
{
    ulong NodeId { get; }

    /// <summary>Messages into the cluster, as business code sends them.</summary>
    ICluster Cluster { get; }

    /// <summary>The current membership snapshot.</summary>
    ClusterView View { get; }

    ILogger Logger { get; }

    /// <summary>Messages from outside the cluster (with an origin and a capped deadline).</summary>
    DaServer.Ingress.IIngress Ingress { get; }

    /// <summary>
    /// Handle the messages sent to <typeparamref name="TAddr"/> addresses this node holds (an <see cref="INodeAddress"/>
    /// whose node is this one, or an <see cref="IGroupAddress"/> fanned out to it). Only from <see cref="NodeFeature.Start"/>.
    /// The node advertises the scope in its membership card, so group messages reach the nodes that registered it.
    /// </summary>
    void HandleAddress<TAddr>(IAddressHandler handler) where TAddr : struct, IAddress;
}

/// <summary>Receives the messages to one address type (<see cref="INodeContext.HandleAddress{TAddr}"/>).</summary>
public interface IAddressHandler
{
    /// <summary>
    /// A message to an address of this node, on the thread that received it (a link's IO thread, or the sender's own
    /// thread for a local send): never block. <paramref name="frame"/> is the raw frame (header, payload, trailers), valid
    /// during the call only. <paramref name="replyTo"/> is set when the message is a request.
    /// </summary>
    void OnMessage(in FrameHeader header, ReadOnlySpan<byte> frame, IReplySink? replyTo);
}
