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
        Options = options;
        Id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
        Logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("DaServer.Node." + options.Name);
        Pending = new PendingCallTable(options.PendingCallBits);
        Membership = new Membership(this);
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
    private CardEntry[] _cardEntries = [];

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

    /// <summary>Start shards, listeners and membership. The node is Up when this returns.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Node already started");
        ActorSessions.Register();

        ResolveHosting();
        foreach (var feature in Options.Features) feature.Start(this);
        _addressHandlers = _registering!.ToFrozenDictionary();
        _registering = null;

        _clusterListener = new Listener(Options.ClusterBindEndPoint,
            socket => new Connection(socket, ClusterHandler, ConnectionOptions.ForNodeLinks()));
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
        Logger.LogInformation("Node {Name} ({Id:x}) up on {Endpoint}, {Shards} shards; actors: {Actors}; services: {Services}; systems: {Systems}",
            Options.Name, Id, ClusterEndPoint, shardCount,
            Names(HostedHandlerTypes.Where(static i => i.IsActor).Select(static i => i.Type.Name)),
            Names(HostedHandlerTypes.Where(static i => !i.IsActor).Select(static i => i.Type.Name)),
            Names(HostedSystems.Select(static s => s.Type.Name)));
        return Task.CompletedTask;
    }

    /// <summary>The host other nodes reach this node at.</summary>
    internal string AdvertisedHost =>
        Options.AdvertisedHost ?? (Options.ListenAddress == "0.0.0.0" ? IPAddress.Loopback.ToString() : Options.ListenAddress);

    /// <summary>What this node hosts, for the startup log (a node hosts what its project compiles, ADR-017).</summary>
    private static string Names(IEnumerable<string> names) => string.Join(", ", names) is { Length: > 0 } s ? s : "none";

    /// <summary>
    /// Graceful shutdown: announce Leaving (so callers stop routing here), drain shards (in-flight calls, actor
    /// unload), then close links.
    /// </summary>
    /// <summary>
    /// Ready for work: started, Up in its own view and not leaving (the readiness health check: platforms route
    /// clients and traffic only to ready nodes).
    /// </summary>
    public bool IsReady => Volatile.Read(ref _started) == 1 && View.TryGetMember(Id, out var self) && self.Status == MemberStatus.Up;

    /// <summary>Called after each phase of a graceful stop with its name and duration (the shutdown metric).</summary>
    internal event Action<string, TimeSpan>? StopPhase;

    /// <summary>
    /// Leave the cluster gracefully within <see cref="NodeOptions.ShutdownTimeout"/> (or until
    /// <paramref name="cancellationToken"/>, the host's own limit, fires): announce Leaving so callers reroute, drain the
    /// features (client connections close their sessions), unload every actor (<see cref="Actor.OnUnload"/>), close the
    /// links. Each phase is logged with its duration; actors still loaded at the deadline are reported, not waited for.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 2) != 1)
            return;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.ShutdownTimeout);
        var phase = System.Diagnostics.Stopwatch.StartNew();

        // Leave() pushes Leaving to every member directly; give those frames time to be delivered (not a gossip
        // round: that can be long), and skip the wait when there is nobody to tell.
        Membership.Leave();
        bool hasPeers = false;
        foreach (var member in View.Members) hasPeers |= member.NodeId != Id;
        if (hasPeers)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(Options.GossipInterval.TotalMilliseconds, 500)), budget.Token)
                .ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        Phase("leave", phase);

        foreach (var feature in Options.Features) await feature.DrainAsync(budget.Token).ConfigureAwait(false);
        Phase("drain", phase);

        var stopped = StopShards();
        await Task.WhenAny(stopped, Task.Delay(Timeout.Infinite, budget.Token)).ConfigureAwait(false);
        if (!stopped.IsCompleted) ReportUnloaded();
        Phase("unload", phase);

        foreach (var feature in Options.Features) feature.Stop(crashed: false);
        CloseLinks();
        Phase("close", phase);
        Logger.LogInformation("Node {Name} ({Id:x}) stopped", Options.Name, Id);
    }

    private void Phase(string name, System.Diagnostics.Stopwatch watch)
    {
        var elapsed = watch.Elapsed;
        watch.Restart();
        Logger.LogInformation("Stop {Phase} of {Node}: {Ms} ms", name, Options.Name, (long)elapsed.TotalMilliseconds);
        StopPhase?.Invoke(name, elapsed);
    }

    /// <summary>The budget ran out while actors were unloading: say how many were not saved (their shards keep trying).</summary>
    /// <summary>
    /// After <see cref="StopAsync"/>: whether every actor was unloaded (and its record written) within the stop budget.
    /// The host exits with an error otherwise, so the platform sees a stop that lost state.
    /// </summary>
    public bool StoppedClean { get; private set; } = true;

    private void ReportUnloaded()
    {
        StoppedClean = false;
        foreach (var shard in _shards)
            if (shard.ActorCount > 0)
                Logger.LogError("Shutdown budget ({Budget}) exceeded: shard {Shard} of {Node} still holds {Actors} actors whose " +
                                "OnUnload did not finish (their state since the last save is lost)", Options.ShutdownTimeout,
                    shard.Index, Options.Name, shard.ActorCount);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private volatile bool _killed;

    /// <summary>
    /// Tests: simulate a process crash. Gossip stops and every socket drops without announcing Leaving, closing client
    /// sessions or unloading actors: the cluster finds out by failure detection only.
    /// </summary>
    internal async Task KillAsync()
    {
        if (Interlocked.Exchange(ref _started, 2) != 1) return;
        _killed = true;
        CloseLinks();
        foreach (var feature in Options.Features) feature.Stop(crashed: true);
        await StopShards().ConfigureAwait(false);
    }

    /// <summary>The node downed itself (minority side of a split brain): the host exits so the platform restarts it.</summary>
    public event Action? Downed;

    /// <summary>
    /// Membership found this node on the minority side: stop at once without announcing Leaving or saving (the majority
    /// owns the keys now and may already run the actors), then tell the host.
    /// </summary>
    internal async Task DownAsync(int reachable, int lost)
    {
        if (Interlocked.Exchange(ref _started, 2) != 1) return;
        Logger.LogCritical("Node {Name} reaches {Reachable} of the cluster's members and lost {Lost}: on the minority side " +
                           "of a split, it stops without saving and exits", Options.Name, reachable, lost);
        _killed = true;
        CloseLinks();
        foreach (var feature in Options.Features) feature.Stop(crashed: true);
        await StopShards(fenced: true).ConfigureAwait(false);
        Downed?.Invoke();
    }

    private Task StopShards(bool fenced = false)
    {
        foreach (var shard in _shards) shard.RequestStop(fenced);
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
            handlers.Add(new HostedMessage { MessageId = handler.MessageId, HandlerTypeId = handlerType.Id, Actor = handlerType.IsActor });
        return handlers.ToArray();
    }

    /// <summary>Active loaded actors across all shards (approximate, for metrics).</summary>
    public int ActorCount => (int)Sum(static s => s.ActorCount);

    /// <summary>Messages waiting in all shard mailboxes (approximate, for metrics and load reporting).</summary>
    public int TotalQueueLength() => (int)Sum(static s => s.QueueLength);

    /// <summary>Calls and events refused by full shard dispatch queues or actor mailboxes since start (backpressure).</summary>
    public long Rejected => Sum(static s => s.Rejected);

    /// <summary>Requests dropped unrun since start because their caller had already timed out.</summary>
    public long Expired => Sum(static s => s.Expired);

    /// <summary>Messages whose handler failed since start (all shards).</summary>
    public long Failed => Sum(static s => s.Failed);

    private long Sum(Func<Shard, long> value)
    {
        long total = 0;
        foreach (var shard in _shards) total += value(shard);
        return total;
    }

    private ulong[] _clientNodes = [];

    /// <summary>
    /// Called by membership when the view changes: shards drop actors they no longer own, and actors that knew sessions
    /// of a client-facing node that died (it could not close them) receive <see cref="SessionClosed"/> for them.
    /// </summary>
    internal void OnViewChanged()
    {
        foreach (var shard in _shards) shard.RequestRebalance();

        // Calls waiting on a dead member's link fail now instead of at their timeout (its stream cannot resume).
        var view = View;
        foreach (var member in view.Members)
            if (member.Status == MemberStatus.Dead)
            {
                _introduced.TryRemove(member.NodeId, out _);
                if (_links.TryGetValue((member.Host, member.Port), out var link)) link.OnPeerDead();
                if (_deadReported.Add(member.NodeId))
                    foreach (var feature in Options.Features) feature.OnMemberDead(member);
            }

        foreach (var feature in Options.Features) feature.OnViewChanged(view);

        var clientNodes = new List<ulong>();
        foreach (var member in View.Members)
            if (member.HasScope(AddressScopes.Session) && member.Status != MemberStatus.Dead) clientNodes.Add(member.NodeId);
        foreach (ulong previous in _clientNodes)
        {
            if (clientNodes.Contains(previous)) continue;
            Logger.LogWarning("Client-facing node {Node:x} is gone: closing its sessions", previous);
            _ = CloseSessionsOf(previous);
        }

        _clientNodes = clientNodes.ToArray();
    }

    /// <summary>How long a lost client may take to resume elsewhere before its actors learn it is gone (tickets need the secret).</summary>
    private TimeSpan ResumeGrace => Options.SigningSecret is { Length: > 0 } ? Options.ResumeGrace : TimeSpan.Zero;

    /// <summary>Close a lost client-facing node's sessions on every shard, once their clients had the grace to resume elsewhere.</summary>
    private async Task CloseSessionsOf(ulong clientNode)
    {
        if (ResumeGrace > TimeSpan.Zero) await Task.Delay(ResumeGrace).ConfigureAwait(false);
        if (Volatile.Read(ref _started) != 1) return;
        foreach (var shard in _shards)
            shard.Post(static s =>
            {
                var (target, clientNode) = ((Shard, ulong))s!;
                ActorSessions.CloseSessionsOf(target.World, clientNode);
            }, (shard, clientNode));
    }

    private long _lastMaintainMs; // the maintenance timer's thread only
    private long[]? _stallReported; // per shard: the pass already reported (the maintenance timer's thread only)
    private int _stalledShards;

    /// <summary>Shards whose current pass has run longer than <see cref="NodeOptions.StallAfter"/>.</summary>
    public int StalledShards => Volatile.Read(ref _stalledShards);

    /// <summary>The stall watchdog: a shard thread away from its loop longer than the threshold is reported once per pass.</summary>
    private void CheckStalls(long now)
    {
        var reported = _stallReported ??= new long[_shards.Length];
        long limit = (long)Options.StallAfter.TotalMilliseconds;
        int stalled = 0;
        for (int i = 0; i < _shards.Length; i++)
        {
            long ms = _shards[i].PassMs(now);
            if (ms < limit) continue;
            stalled++;
            long passStart = now - ms;
            if (reported[i] == passStart) continue;
            reported[i] = passStart;
            Logger.LogWarning("Shard {Shard} of {Node} has been in one pass for {Ms} ms: a handler or system blocks its thread " +
                              "(a synchronous wait, DAS009, or a long loop) and its actors wait", i, Options.Name, ms);
        }

        Volatile.Write(ref _stalledShards, stalled);
    }

    private void Maintain()
    {
        try
        {
            Pending.SweepExpired();
            long now = Environment.TickCount64, last = _lastMaintainMs;
            _lastMaintainMs = now;
            long interval = (long)Options.GossipInterval.TotalMilliseconds;
            // A pass that runs late (a GC pause, a starved machine) says nothing about the links: ping, no verdicts.
            long timeout = last != 0 && now - last > 1000 ? long.MaxValue : Math.Max((long)Options.SuspectAfter.TotalMilliseconds, 3 * interval);
            foreach (var link in Volatile.Read(ref _linkList))
            {
                link.AcknowledgeReceived();
                link.CheckAlive(now, interval, timeout);
            }

            CheckStalls(now);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Maintenance failed");
        }
    }
}
