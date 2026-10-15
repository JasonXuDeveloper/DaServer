using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaServer.Runtime;

/// <summary>
/// One process of the cluster: owns the shards, the cluster listener and links, membership and placement, and routes
/// every message (<see cref="ICluster"/>).
/// </summary>
public sealed partial class Node : ICluster, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string Host, int Port), PeerLink> _links = new();
    private PeerLink[] _linkList = [];
    private readonly ConcurrentDictionary<ulong, PeerLink> _introduced = new(); // node id -> link, from Hellos
    private Shard[] _shards = [];
    private FrozenDictionary<ulong, ImplementationInfo?> _requestHandlers = FrozenDictionary<ulong, ImplementationInfo?>.Empty;
    private FrozenDictionary<uint, ImplementationInfo> _implementationById = FrozenDictionary<uint, ImplementationInfo>.Empty;
    private Listener? _clusterListener;
    private Timer? _maintenance;
    private int _started;

    public Node(NodeOptions options)
    {
        Options = options;
        Id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
        Logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DaServer.Node." + options.Name);
        Pending = new PendingCallTable(options.PendingCallBits);
        Membership = new Membership(this);
        _localSink = new LocalReplySink(this);
    }

    public NodeOptions Options { get; }

    /// <summary>Random 64-bit id, new on every process start (a restarted node is a new member).</summary>
    public ulong Id { get; }

    ulong ICluster.NodeId => Id;

    public ILogger Logger { get; }

    /// <summary>Endpoint other nodes use to reach this node (valid after <see cref="StartAsync"/>).</summary>
    public IPEndPoint ClusterEndPoint { get; private set; } = new(IPAddress.Loopback, 0);

    public IReadOnlyList<Shard> Shards => _shards;

    /// <summary>Current routing snapshot.</summary>
    public ClusterView View => Membership.View;

    internal PendingCallTable Pending { get; }
    internal Membership Membership { get; }
    internal ImplementationInfo[] HostedImplementations { get; private set; } = [];
    internal SystemInfo[] HostedSystems { get; private set; } = [];

    /// <summary>This node owns the actor <paramref name="key"/> of <paramref name="implementationId"/> in the current view.</summary>
    internal bool IsLocalOwner(uint implementationId, long key) => View.ImplementationOwner(implementationId, key)?.NodeId == Id;

    /// <summary>Start shards, listeners and membership. The node is Up when this returns.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Node already started");

        ResolveHosting();

        _clusterListener = new Listener(Options.ClusterBindEndPoint,
            socket => new Connection(socket, ClusterHandler, ConnectionOptions.ForNodeLinks()));
        _clusterListener.Start();
        var host = Options.AdvertisedHost ??
                   (Options.ListenAddress == "0.0.0.0" ? IPAddress.Loopback.ToString() : Options.ListenAddress);
        ClusterEndPoint = new IPEndPoint(IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback,
            _clusterListener.LocalEndPoint.Port);

        int shardCount = Math.Max(1, Options.Shards);
        _shards = new Shard[shardCount];
        for (int i = 0; i < shardCount; i++)
            _shards[i] = new Shard(this, i, Options.ShardInboxCapacity, Options.TickRate);
        foreach (var shard in _shards) shard.Start();

        StartGateway();

        Membership.Start(host, _clusterListener.LocalEndPoint.Port);
        _maintenance = new Timer(static s => ((Node)s!).Maintain(), this, 100, 100);
        Logger.LogInformation("Node {Name} ({Id:x}) up on {Endpoint}, {Shards} shards; actors: {Actors}; services: {Services}; systems: {Systems}",
            Options.Name, Id, ClusterEndPoint, shardCount,
            Names(HostedImplementations.Where(static i => i.IsActor).Select(static i => i.Type.Name)),
            Names(HostedImplementations.Where(static i => !i.IsActor).Select(static i => i.Type.Name)),
            Names(HostedSystems.Select(static s => s.Type.Name)));
        return Task.CompletedTask;
    }

    /// <summary>What this node hosts, for the startup log (a node hosts what its project compiles, ADR-017).</summary>
    private static string Names(IEnumerable<string> names) => string.Join(", ", names) is { Length: > 0 } s ? s : "none";

    /// <summary>
    /// Graceful shutdown: announce Leaving (so callers stop routing here), drain shards (in-flight calls, actor
    /// deactivation), then close links.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 2) != 1)
            return;

        // Leave() pushes Leaving to every member directly; give those frames time to be delivered (not a gossip
        // round: that can be long), and skip the wait when there is nobody to tell.
        Membership.Leave();
        bool hasPeers = false;
        foreach (var member in View.Members) hasPeers |= member.NodeId != Id;
        if (hasPeers)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(Options.GossipInterval.TotalMilliseconds, 500))).ConfigureAwait(false);

        StopGateway();
        await StopShards().ConfigureAwait(false);
        CloseLinks();
        Logger.LogInformation("Node {Name} ({Id:x}) stopped", Options.Name, Id);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private volatile bool _killed;

    /// <summary>
    /// Tests: simulate a process crash. Gossip stops and every socket drops without announcing Leaving, closing client
    /// sessions or deactivating actors: the cluster finds out by failure detection only.
    /// </summary>
    internal async Task KillAsync()
    {
        if (Interlocked.Exchange(ref _started, 2) != 1) return;
        _killed = true;
        CloseLinks();
        StopGateway();
        await StopShards().ConfigureAwait(false);
    }

    private Task StopShards()
    {
        foreach (var shard in _shards) shard.RequestStop();
        return Task.WhenAll(Array.ConvertAll(_shards, static s => s.Stopped));
    }

    /// <summary>Stop gossip, maintenance and every node link.</summary>
    private void CloseLinks()
    {
        _maintenance?.Dispose();
        Membership.Dispose();
        _clusterListener?.Dispose();
        foreach (var link in _links.Values) link.Dispose();
    }

    /// <summary>Hosted implementation with placement/routing id <paramref name="implementationId"/>, or null.</summary>
    internal ImplementationInfo? ImplementationById(uint implementationId) =>
        _implementationById.TryGetValue(implementationId, out var impl) ? impl : null;

    /// <summary>
    /// The hosted implementation answering requests of <paramref name="messageId"/> sent to an actor or a service
    /// (requests carry the message id, not the implementation: their RequestId field is the call's), or null when none
    /// or several of that kind handle it (an event: published to all of them, never asked).
    /// </summary>
    internal ImplementationInfo? RequestHandler(uint messageId, bool actor) =>
        _requestHandlers.TryGetValue(RequestKey(messageId, actor), out var impl) ? impl : null;

    private static ulong RequestKey(uint messageId, bool actor) => (ulong)messageId << 1 | (actor ? 1UL : 0UL);

    /// <summary>Everything registered in the process, narrowed by <see cref="NodeOptions.Hosts"/> when set.</summary>
    private void ResolveHosting()
    {
        var hosts = Options.Hosts;
        var byId = new Dictionary<uint, ImplementationInfo>();
        var requests = new Dictionary<ulong, ImplementationInfo?>();
        var hosted = new List<ImplementationInfo>();
        foreach (var impl in ServerRegistry.Implementations)
        {
            if (hosts != null && !hosts(impl.Type)) continue;
            hosted.Add(impl);
            byId[impl.Id] = impl;
            foreach (var handler in impl.Handlers.All)
            {
                ulong key = RequestKey(handler.MessageId, impl.IsActor);
                requests[key] = requests.ContainsKey(key) ? null : impl;
            }
        }

        var systems = new List<SystemInfo>();
        foreach (var system in ServerRegistry.Systems)
            if (hosts == null || hosts(system.Type))
                systems.Add(system);

        HostedImplementations = hosted.ToArray();
        HostedSystems = systems.ToArray();
        _requestHandlers = requests.ToFrozenDictionary();
        _implementationById = byId.ToFrozenDictionary();
    }

    /// <summary>What this node advertises in membership: the messages its implementations handle.</summary>
    internal HostedHandler[] AdvertisedHandlers()
    {
        var handlers = new List<HostedHandler>();
        foreach (var impl in HostedImplementations)
        foreach (var handler in impl.Handlers.All)
            handlers.Add(new HostedHandler { MessageId = handler.MessageId, ImplementationId = impl.Id, Actor = impl.IsActor });
        return handlers.ToArray();
    }

    /// <summary>Active actor activations across all shards (approximate, for metrics).</summary>
    public int ActorCount => (int)Sum(static s => s.ActorCount);

    /// <summary>Messages waiting in all shard mailboxes (approximate, for metrics and load reporting).</summary>
    public int TotalQueueLength() => (int)Sum(static s => s.QueueLength);

    /// <summary>Calls and events refused by full shard inboxes or actor mailboxes since start (backpressure).</summary>
    public long Rejected => Sum(static s => s.Rejected);

    /// <summary>Requests dropped unrun since start because their caller had already timed out.</summary>
    public long Expired => Sum(static s => s.Expired);

    private long Sum(Func<Shard, long> value)
    {
        long total = 0;
        foreach (var shard in _shards) total += value(shard);
        return total;
    }

    private ulong[] _gateways = [];

    /// <summary>
    /// Called by membership when the view changes: shards drop actors they no longer own, and actors that knew sessions
    /// of a gateway that died (it could not close them) receive <see cref="Rpc.SessionClosed"/> for them.
    /// </summary>
    internal void OnViewChanged()
    {
        foreach (var shard in _shards) shard.RequestRebalance();

        // Calls waiting on a dead member's link fail now instead of at their timeout (its stream cannot resume).
        foreach (var member in View.Members)
            if (member.Status == MemberStatus.Dead)
            {
                _introduced.TryRemove(member.NodeId, out _);
                if (_links.TryGetValue((member.Host, member.Port), out var link)) link.OnPeerDead();
            }

        var gateways = new List<ulong>();
        foreach (var member in View.Members)
            if (member.ClientPort != 0 && member.Status != MemberStatus.Dead) gateways.Add(member.NodeId);
        foreach (ulong previous in _gateways)
        {
            if (gateways.Contains(previous)) continue;
            Logger.LogWarning("Gateway {Gateway:x} is gone: closing its sessions", previous);
            _ = CloseSessionsOf(previous);
        }

        _gateways = gateways.ToArray();
    }

    /// <summary>Close a lost gateway's sessions on every shard, once their clients had the grace to resume elsewhere.</summary>
    private async Task CloseSessionsOf(ulong gateway)
    {
        if (ReconnectGrace > TimeSpan.Zero) await Task.Delay(ReconnectGrace).ConfigureAwait(false);
        if (Volatile.Read(ref _started) != 1) return;
        foreach (var shard in _shards)
            shard.Post(static s =>
            {
                var (target, gateway) = ((Shard, ulong))s!;
                ShardDispatch.CloseSessionsOf(target.World, gateway);
            }, (shard, gateway));
    }

    private void Maintain()
    {
        try
        {
            Pending.SweepExpired();
            foreach (var link in Volatile.Read(ref _linkList)) link.AcknowledgeReceived();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Maintenance failed");
        }
    }
}
