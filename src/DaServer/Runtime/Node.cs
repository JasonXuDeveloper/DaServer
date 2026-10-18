using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaServer.Runtime;

/// <summary>
/// One process of the cluster: owns the shards, the cluster listener and links, membership and placement, and routes
/// every message (<see cref="ICluster"/>).
/// </summary>
internal sealed partial class Node : ICluster, INodeContext, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string Host, int Port), PeerLink> _links = new();
    private PeerLink[] _linkList = [];
    private readonly ConcurrentDictionary<ulong, PeerLink> _introduced = new(); // node id -> link, from Hellos
    private readonly LinkAuth _linkAuth;

    /// <summary>An accepted connection that did not start with an authentic Hello of this cluster.</summary>
    private static readonly InvalidOperationException NotIntroduced = new("Not introduced by an authentic Hello");
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
        Pending = new PendingCallTable(options.PendingCallBits);
        Membership = new Membership(this);
        _sessionNodes = new SessionNodes(this);
        _slots = new SlotAllocation(this);
        _localSink = new LocalReplySink(this);
        _linkAuth = new LinkAuth(options.ClusterName, options.SigningSecret);
    }

    public NodeOptions Options { get; }

    /// <summary>Random 64-bit id, new on every process start (a restarted node is a new member).</summary>
    public ulong Id { get; }

    ulong ICluster.NodeId => Id;

    ulong INodeContext.NodeId => Id;

    ICluster INodeContext.Cluster => this;

    void INodeContext.HandleAddress<TAddr>(IAddressHandler handler)
    {
        var registering = _registering ?? throw new InvalidOperationException("Address handlers are registered in NodeFeature.Start");
        if (!registering.TryAdd(Addresses<TAddr>.Scope, handler))
            throw new InvalidOperationException($"{typeof(TAddr)} already has a handler on this node");
    }

    private uint[] _pulseKeys = [];
    private Func<int>[] _pulseSources = [];
    private CardEntry[] _cardEntries = [new(ClusterMember.BuildKey, Build)];

    /// <summary>
    /// What this node runs (V2): the framework's and the application's informational versions, on the card so a
    /// rolling upgrade is visible in every view (<see cref="ClusterMember.Build"/>).
    /// </summary>
    internal static readonly string Build = "daserver " + VersionOf(typeof(Node).Assembly) + "; app " +
        VersionOf(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Node).Assembly);

    private static string VersionOf(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute v, ..]
            ? v.InformationalVersion
            : assembly.GetName().Version?.ToString() ?? "unknown";

    void INodeContext.AddPulse(uint key, Func<int> read)
    {
        if (_registering == null) throw new InvalidOperationException("Pulses are added in NodeFeature.Start");
        if (_pulseKeys.Length == MemberPulse.Slots) throw new InvalidOperationException($"A node gossips at most {MemberPulse.Slots} feature counters");
        _pulseKeys = [.. _pulseKeys, key];
        _pulseSources = [.. _pulseSources, read];
    }

    /// <summary>Gauges the node's features report (<see cref="INodeContext.AddGauge"/>).</summary>
    internal (string Name, string Description, Func<long> Read)[] Gauges { get; private set; } = [];

    void INodeContext.AddGauge(string name, string description, Func<long> read)
    {
        if (_registering == null) throw new InvalidOperationException("Gauges are added in NodeFeature.Start");
        Gauges = [.. Gauges, (name, description, read)];
    }

    void INodeContext.SetCardEntry(uint key, string value)
    {
        if (_registering == null) throw new InvalidOperationException("Card entries are set in NodeFeature.Start");
        _cardEntries = [.. _cardEntries, new CardEntry(key, value)];
    }

    void INodeContext.PostToShards(Action<World, object?> callback, object? state)
    {
        foreach (var shard in _shards)
            shard.Post(static s =>
            {
                var (target, callback, state) = ((Shard, Action<World, object?>, object?))s!;
                callback(target.World, state);
            }, (shard, callback, state));
    }

    /// <summary>What this node's features publish on its card and gossip in its pulse.</summary>
    internal (uint[] PulseKeys, CardEntry[] Entries) FeatureCard() => (_pulseKeys, _cardEntries);

    /// <summary>The features' counters for this round's pulse (gossip timer).</summary>
    internal void ReadPulses(int[] values)
    {
        var sources = _pulseSources;
        for (int i = 0; i < sources.Length && i < values.Length; i++) Volatile.Write(ref values[i], sources[i]());
    }

    void INodeContext.AddIngressFilter(IIngressFilter filter)
    {
        if (_registering == null) throw new InvalidOperationException("Ingress filters are added in NodeFeature.Start");
        _ingressFilters = [.. _ingressFilters, filter];
    }

    /// <summary>The feature of type <typeparamref name="T"/> this node runs, or null (tools, tests, metrics).</summary>
    internal T? Feature<T>() where T : NodeFeature
    {
        foreach (var feature in Options.Features)
            if (feature is T match) return match;
        return null;
    }

    /// <summary>The address scopes this node's features handle (advertised in its card).</summary>
    internal uint[] AdvertisedScopes() => [.. _addressHandlers.Keys];

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
    /// The views replaced within the handoff timeout, newest first (at most <see cref="RecentView.Kept"/>): who held a key
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

        // From the view, or, before gossip lists it (a node that just joined), from the Hello it introduced itself with.
        var link = View.TryGetMember(nodeId, out var member) ? member.Status != MemberStatus.Dead ? GetLink(member) : null
            : _introduced.TryGetValue(nodeId, out var introduced) ? introduced : null;
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
        ActorSessions.Register();

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


    /// <summary>Messages whose handler failed since start (all shards).</summary>
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

    /// <summary>
    /// Called by membership when the view changes: shards drop actors they no longer own, and actors that knew sessions
    /// of a client-facing node that died (it could not close them) receive <see cref="SessionClosed"/> for them.
    /// </summary>
    /// <summary>Whether a member other than <paramref name="dead"/> lives at its address (it restarted there).</summary>
    private static bool LiveAt(ClusterView view, ClusterMember dead)
    {
        foreach (var member in view.Members)
            if (member.NodeId != dead.NodeId && member.Status != MemberStatus.Dead && member.Host == dead.Host && member.Port == dead.Port)
                return true;
        return false;
    }

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
                if (_deadReported.Add(member.NodeId))
                    foreach (var feature in Options.Features) feature.OnMemberDead(member);
            }
        }

        foreach (var feature in Options.Features) feature.OnViewChanged(view);
        ReportBuilds(view);

        _sessionNodes.OnViewChanged(view);
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
