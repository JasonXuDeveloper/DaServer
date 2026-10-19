using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaServer.Runtime;

/// <summary>
/// One process of the cluster: owns the shards, the cluster listener and links, membership and placement, and routes
/// every message (<see cref="ICluster"/>).
/// </summary>
internal sealed partial class Node : ICluster, INodeContext, IAsyncDisposable
{
    private Shard[] _shards = [];
    private FrozenDictionary<uint, HandlerTypeInfo> _handlerTypeById = FrozenDictionary<uint, HandlerTypeInfo>.Empty;
    private Dictionary<uint, IAddressHandler>? _registering = new(); // address handlers while features start
    private FrozenDictionary<uint, IAddressHandler> _addressHandlers = FrozenDictionary<uint, IAddressHandler>.Empty;
    private readonly HashSet<ulong> _deadReported = new(); // members whose death features were told (view changes are serialized)
    private Listener? _clusterListener;
    private Timer? _maintenance;
    private int _started;

    public Node(NodeOptions options)
    {
        options.Validate();
        Options = options;
        Id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
        Logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DaServer.Node." + options.Name);
        Pending = new PendingCallTable(options.PendingCallBits, Stripes()); // shards, IO threads and outside callers at once
        Membership = new Membership(this);
        _sessionNodes = new SessionNodes(this);
        _slots = new SlotAllocation(this);
        _localSink = new LocalReplySink(this);
        SigningKeys = SigningKeys.Of(options);
        _linkAuth = new LinkAuth(options.ClusterName, SigningKeys);
    }

    public NodeOptions Options { get; }

    /// <summary>Random 64-bit id, new on every process start (a restarted node is a new member).</summary>
    public ulong Id { get; }

    ulong ICluster.NodeId => Id;

    ulong INodeContext.NodeId => Id;

    ICluster INodeContext.Cluster => this;

    public ILogger Logger { get; }

    /// <summary>Endpoint other nodes use to reach this node (valid after <see cref="StartAsync"/>).</summary>
    public IPEndPoint ClusterEndPoint { get; private set; } = new(IPAddress.Loopback, 0);

    public IReadOnlyList<Shard> Shards => _shards;

    /// <summary>Current routing snapshot.</summary>
    public ClusterView View => Membership.View;

    internal PendingCallTable Pending { get; }
    internal Membership Membership { get; }
    internal HandlerTypeInfo[] HostedHandlerTypes { get; private set; } = [];
    internal SystemInfo[] HostedSystems { get; private set; } = [];

    /// <summary>This node owns the actor <paramref name="key"/> of <paramref name="handlerTypeId"/> in the current view.</summary>
    internal bool IsLocalOwner(uint handlerTypeId, long key) => View.HandlerTypeOwner(handlerTypeId, key)?.NodeId == Id;

    private ClusterView? _lastView;
    private RecentView[] _recentViews = [];

    /// <summary>
    /// The views replaced within the move window (<see cref="Handoff.MoveWindowMs"/>), newest first (at most <see cref="RecentView.Kept"/>): who held a key
    /// before it moved here, and whether its record may still be on its way (<see cref="Handoff"/>). A snapshot.
    /// </summary>
    internal RecentView[] RecentViews => Volatile.Read(ref _recentViews);

    /// <summary>
    /// Send a frame (header written) to node <paramref name="nodeId"/>, to the shard of its actor: through the link to it
    /// (in order with everything else this node sends there), or to a local shard. False when the node is not in the view.
    /// Takes ownership of <paramref name="frame"/>.
    /// </summary>
    internal bool SendToNode(ulong nodeId, FrameWriter frame)
    {
        if (nodeId == Id)
        {
            var header = FrameHeader.Read(frame.Frame);
            Admit(SelectShard(header.Target, actor: true, header.Key), frame, null);
            return true;
        }

        var link = LinkTo(nodeId);
        link?.Send(frame.Frame);
        frame.Dispose();
        return link != null;
    }

    /// <summary>Start shards, listeners and membership. The node is Up when this returns.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Node already started");
        // The store first: a database that is down or a table that is missing stops the node here, saying so, before it
        // joins and takes keys (not at its first load, with callers waiting).
        if (Options.ActorStore is { } store) await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        ResolveHosting();
        foreach (var feature in Options.Features) feature.Start(this);
        _addressHandlers = _registering!.ToFrozenDictionary();
        _registering = null;

        _clusterListener = new Listener(Options.ClusterBindEndPoint, Accept);
        _clusterListener.Start();
        var host = AdvertisedHost;
        ClusterEndPoint = new IPEndPoint(IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback,
            _clusterListener.LocalEndPoint.Port);

        int shardCount = Math.Max(1, Options.Shards);
        _shards = new Shard[shardCount];
        for (int i = 0; i < shardCount; i++)
            _shards[i] = new Shard(this, i, Options.DispatchQueueCapacity, Options.TickRate);
        foreach (var shard in _shards) shard.Start();

        Membership.Start(host, _clusterListener.LocalEndPoint.Port);
        _maintenance = new Timer(static s => ((Node)s!).Maintain(), this, 100, 100);
        Log.NodeUp(Logger, Options.Name, Id, ClusterEndPoint, shardCount,
            Names(HostedHandlerTypes.Where(static i => i.IsActor).Select(static i => i.Type.Name)),
            Names(HostedHandlerTypes.Where(static i => !i.IsActor).Select(static i => i.Type.Name)),
            Names(HostedSystems.Select(static s => s.Type.Name)));
    }

    /// <summary>
    /// The host other nodes reach this node at: the configured one, else the bind address, else (listening on every
    /// address) the container's own address in a container (each replica of a published workload advertises itself:
    /// a pod's IP, a Compose replica's) and loopback outside one (a local run).
    /// </summary>
    internal string AdvertisedHost =>
        Options.AdvertisedHost ?? (Options.ListenAddress != "0.0.0.0" ? Options.ListenAddress
            : InContainer && OwnAddress() is { } own ? own.ToString() : IPAddress.Loopback.ToString());

    /// <summary>Set by the .NET container images (and Aspire's published projects).</summary>
    private static bool InContainer => Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    /// <summary>The first IPv4 address of an interface that is up and not loopback (a container has one).</summary>
    private static IPAddress? OwnAddress()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
                if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return address.Address;
        }

        return null;
    }

    /// <summary>What this node hosts, for the startup log (a node hosts what its project compiles, ADR-017).</summary>
    private static string Names(IEnumerable<string> names) => string.Join(", ", names) is { Length: > 0 } s ? s : "none";

    /// <summary>
    /// Ready for work: started, Up in its own view and not leaving (the readiness health check: platforms route
    /// clients and traffic only to ready nodes).
    /// </summary>
    public bool IsReady => Volatile.Read(ref _started) == 1 && View.TryGetMember(Id, out var self) && self.Status == MemberStatus.Up;

    /// <summary>Free stacks of the call table: the processor count rounded up to a power of two, at most 16.</summary>
    private static int Stripes() => (int)Math.Min(16, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount));

    /// <summary>
    /// Unload every loaded actor of the hosted actor type named <paramref name="typeName"/> (its class name or full name;
    /// the admin endpoint): each leaves once idle, saved as on any unload, and its next mail loads it again. False when
    /// this node hosts no such actor type.
    /// </summary>
    internal bool UnloadActors(string typeName)
    {
        foreach (var handlerType in HostedHandlerTypes)
            if (handlerType.IsActor && (handlerType.Type.Name == typeName || handlerType.Type.FullName == typeName))
            {
                foreach (var shard in _shards) shard.RequestUnload(handlerType.Id);
                return true;
            }

        return false;
    }

    /// <summary>Hosted handler type with placement/routing id <paramref name="handlerTypeId"/>, or null.</summary>
    internal HandlerTypeInfo? HandlerTypeById(uint handlerTypeId) =>
        _handlerTypeById.TryGetValue(handlerTypeId, out var handlerType) ? handlerType : null;

    /// <summary>Everything registered in the process, narrowed by <see cref="NodeOptions.Hosts"/> when set.</summary>
    private void ResolveHosting()
    {
        var hosts = Options.Hosts;
        var byId = new Dictionary<uint, HandlerTypeInfo>();
        var hosted = new List<HandlerTypeInfo>();
        foreach (var handlerType in ServerRegistry.HandlerTypes)
        {
            if (hosts != null && !hosts(handlerType.Type)) continue;
            hosted.Add(handlerType);
            byId[handlerType.Id] = handlerType;
        }

        var systems = new List<SystemInfo>();
        foreach (var system in ServerRegistry.Systems)
            if (hosts == null || hosts(system.Type))
                systems.Add(system);

        HostedHandlerTypes = hosted.ToArray();
        HostedSystems = systems.ToArray();
        _handlerTypeById = byId.ToFrozenDictionary();
    }

    /// <summary>What this node advertises in membership: the messages its handler types handle.</summary>
    internal HostedMessage[] AdvertisedHandlers()
    {
        var handlers = new List<HostedMessage>();
        foreach (var handlerType in HostedHandlerTypes)
        foreach (var handler in handlerType.Handlers.All)
            if (Options.Unannounced?.Invoke(handler.MessageId) != true)
                handlers.Add(new HostedMessage { MessageId = handler.MessageId, HandlerTypeId = handlerType.Id, Actor = handlerType.IsActor });
        return handlers.ToArray();
    }

    /// <summary>Active loaded actors across all shards (approximate, for metrics).</summary>
    public int ActorCount => (int)Sum(static s => s.ActorCount);

    /// <summary>Messages waiting in all shard mailboxes (approximate, for metrics and load reporting).</summary>
    public int TotalQueueLength() => (int)Sum(static s => s.QueueLength);

    /// <summary>Messages dropped unrun for <paramref name="reason"/> since start, all shards (backpressure, late requests).</summary>
    public long Dropped(DropReason reason)
    {
        long sum = 0;
        foreach (var shard in _shards) sum += shard.Dropped(reason);
        return sum;
    }

    /// <summary>Time the shards spent asleep waiting for work since start, in <see cref="Stopwatch"/> ticks, all shards.</summary>
    internal long IdleTicks => Sum(static s => s.IdleTicks);

    /// <summary>The shards' ticks that started a whole interval late since start.</summary>
    public long TickOverruns => Sum(static s => s.TickOverruns);

    /// <summary>Shards on this node.</summary>
    public int ShardCount => _shards.Length;


    /// <summary>Messages whose handler failed, and system passes that threw, since start (all shards).</summary>
    public long Failed => Sum(static s => s.Failed);

    private long Sum(Func<Shard, long> value)
    {
        long total = 0;
        foreach (var shard in _shards) total += value(shard);
        return total;
    }

    private readonly SessionNodes _sessionNodes;
    private readonly SlotAllocation _slots;
    private string _builds = "";

    /// <summary>Log when the builds the cluster runs change (a rolling upgrade starts or completes, V2).</summary>
    private void ReportBuilds(ClusterView view)
    {
        var builds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in view.Members)
            if (member.Status != MemberStatus.Dead) builds[member.Build] = builds.GetValueOrDefault(member.Build) + 1;
        string summary = string.Join(" | ", builds.Select(static b => $"{b.Key} x{b.Value}"));
        if (summary == _builds) return;
        bool mixed = builds.Count > 1 || _builds.Contains(" | ", StringComparison.Ordinal);
        _builds = summary;
        if (mixed) Log.Builds(Logger, builds.Count, summary);
    }

    /// <summary>Whether a member other than <paramref name="dead"/> lives at its address (it restarted there).</summary>
    private static bool LiveAt(ClusterView view, ClusterMember dead)
    {
        foreach (var member in view.Members)
            if (member.NodeId != dead.NodeId && member.Status != MemberStatus.Dead && member.Host == dead.Host && member.Port == dead.Port)
                return true;
        return false;
    }

    /// <summary>
    /// Called by membership when the view changes: shards drop actors they no longer own, and actors that knew sessions
    /// of a client-facing node that died (it could not close them) receive <see cref="SessionClosed"/> for them.
    /// </summary>
    internal void OnViewChanged()
    {
        var view = View;
        // Before the shards rebalance: new owners ask the previous ones.
        if (Interlocked.Exchange(ref _lastView, view) is { } replaced) Volatile.Write(ref _recentViews, RecentView.Push(_recentViews, replaced, Options));
        foreach (var shard in _shards) shard.RequestRebalance();

        foreach (var member in view.Members)
        {
            // The view serves every member it lists: the link a node introduced itself on is only for before that (and a
            // member that left is forgotten without ever being dead).
            _introduced.TryRemove(member.NodeId, out _);
            if (member.Status == MemberStatus.Dead)
            {
                // Calls waiting on a dead member's link fail now instead of at their timeout (its stream cannot resume).
                // Links are per address: another process may answer there now (a restart with a new id) and its stream
                // is not the dead one's.
                if (_links.TryGetValue((member.Host, member.Port), out var link) && link.PeerId is var peer &&
                    (peer == member.NodeId || peer == 0 && !LiveAt(view, member)))
                    link.OnPeerDead();
            }
        }

        ReportBuilds(view);
        _sessionNodes.OnViewChanged(view);

        // The features last: the node's own steps above must not depend on them.
        foreach (var member in view.Members)
            if (member.Status == MemberStatus.Dead && _deadReported.Add(member.NodeId))
                ForEachFeature(member, static (f, m) => f.OnMemberDead(m), nameof(NodeFeature.OnMemberDead));
        ForEachFeature(view, static (f, v) => f.OnViewChanged(v), nameof(NodeFeature.OnViewChanged));
    }

    /// <summary>Whether the node still runs (late work, such as a grace that ended, checks it).</summary>
    internal bool Running => Volatile.Read(ref _started) == 1;

    /// <summary>Post <paramref name="work"/> to every shard (rare work: a gone node's sessions).</summary>
    internal void PostToEveryShard<TState>(Action<World, TState> work, TState state)
    {
        foreach (var shard in _shards)
            shard.Post(static s =>
            {
                var (target, work, state) = ((Shard, Action<World, TState>, TState))s!;
                work(target.World, state);
            }, (shard, work, state));
    }
}
