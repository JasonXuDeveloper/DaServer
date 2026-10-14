using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Cluster;
using DaServer.Net;
using DaServer.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaServer.Runtime;

/// <summary>
/// One process of the cluster: owns the shards, the cluster listener and links, membership and placement, and routes
/// every call made through generated proxies (<see cref="IRpcInvoker"/>).
/// </summary>
public sealed partial class Node : ICluster, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string Host, int Port), PeerLink> _links = new();
    private Shard[] _shards = [];
    private FrozenDictionary<uint, ImplementationInfo> _implementationByContract = FrozenDictionary<uint, ImplementationInfo>.Empty;
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
        Placement = new NodePlacement(this);
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
    internal NodePlacement Placement { get; }
    internal ImplementationInfo[] HostedImplementations { get; private set; } = [];
    internal SystemInfo[] HostedSystems { get; private set; } = [];

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
                   (Options.ClusterBind == "0.0.0.0" ? IPAddress.Loopback.ToString() : Options.ClusterBind);
        ClusterEndPoint = new IPEndPoint(IPAddress.TryParse(host, out var ip) ? ip : IPAddress.Loopback,
            _clusterListener.LocalEndPoint.Port);

        int shardCount = Math.Max(1, Options.Shards);
        _shards = new Shard[shardCount];
        for (int i = 0; i < shardCount; i++)
            _shards[i] = new Shard(this, i, Options.MailboxCapacity, Options.TickRate);
        foreach (var shard in _shards) shard.Start();

        StartGateway();

        Membership.Start(host, _clusterListener.LocalEndPoint.Port);
        _maintenance = new Timer(static s => ((Node)s!).Maintain(), this, 100, 100);
        Logger.LogInformation("Node {Name} ({Id:x}) up on {Endpoint}, {Shards} shards, {Implementations} implementations, {Systems} systems",
            Options.Name, Id, ClusterEndPoint, shardCount, HostedImplementations.Length, HostedSystems.Length);
        return Task.CompletedTask;
    }

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
        foreach (var shard in _shards) shard.RequestStop();
        await Task.WhenAll(Array.ConvertAll(_shards, w => w.Stopped)).ConfigureAwait(false);

        _maintenance?.Dispose();
        Membership.Dispose();
        _clusterListener?.Dispose();
        foreach (var link in _links.Values) link.Dispose();
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
        _maintenance?.Dispose();
        Membership.Dispose();
        _clusterListener?.Dispose();
        StopGateway();
        foreach (var link in _links.Values) link.Dispose();
        foreach (var shard in _shards) shard.RequestStop();
        await Task.WhenAll(Array.ConvertAll(_shards, w => w.Stopped)).ConfigureAwait(false);
    }

    /// <summary>Hosted implementation serving <paramref name="contract"/> on this node, or null.</summary>
    internal ImplementationInfo? ImplementationOf(RpcContractInfo contract) =>
        _implementationByContract.TryGetValue(contract.Id, out var impl) ? impl : null;

    /// <summary>Hosted implementation with placement/routing id <paramref name="implementationId"/>, or null.</summary>
    internal ImplementationInfo? ImplementationById(uint implementationId) =>
        _implementationById.TryGetValue(implementationId, out var impl) ? impl : null;

    /// <summary>Everything registered in the process, narrowed by <see cref="NodeOptions.Hosts"/> when set.</summary>
    private void ResolveHosting()
    {
        var hosts = Options.Hosts;
        var byContract = new Dictionary<uint, ImplementationInfo>();
        var byId = new Dictionary<uint, ImplementationInfo>();
        var hosted = new List<ImplementationInfo>();
        foreach (var impl in ServerRegistry.Implementations)
        {
            if (hosts != null && !hosts(impl.Type)) continue;
            hosted.Add(impl);
            byId[impl.Id] = impl;
            foreach (var contractType in impl.Contracts)
            {
                ServerRegistry.EnsureLoaded(contractType);
                var contract = RpcRegistry.GetContract(contractType);
                if (byContract.TryGetValue(contract.Id, out var other))
                    throw new InvalidOperationException($"{contract} is implemented by both {other} and {impl}");
                byContract[contract.Id] = impl;
            }
        }

        var systems = new List<SystemInfo>();
        foreach (var system in ServerRegistry.Systems)
            if (hosts == null || hosts(system.Type))
                systems.Add(system);

        HostedImplementations = hosted.ToArray();
        HostedSystems = systems.ToArray();
        _implementationByContract = byContract.ToFrozenDictionary();
        _implementationById = byId.ToFrozenDictionary();
    }

    /// <summary>What this node advertises in membership: services, actor implementations and event handlers.</summary>
    internal (uint[] Services, HostedActor[] Actors, HostedHandler[] Handlers) AdvertisedContracts()
    {
        var services = new List<uint>();
        var actors = new List<HostedActor>();
        var handlers = new List<HostedHandler>();
        foreach (var impl in HostedImplementations)
        {
            bool isActor = impl.Kind == RpcContractKind.Actor;
            foreach (var contractType in impl.Contracts)
            {
                uint contractId = RpcRegistry.GetContract(contractType).Id;
                if (isActor) actors.Add(new HostedActor { ContractId = contractId, ImplementationId = impl.Id });
                else services.Add(contractId);
            }

            if (isActor && impl.Contracts.Length == 0)
                actors.Add(new HostedActor { ContractId = 0, ImplementationId = impl.Id });
            foreach (var handler in impl.Events)
                handlers.Add(new HostedHandler { EventId = handler.EventId, ImplementationId = impl.Id, Actor = isActor });
        }

        return (services.ToArray(), actors.ToArray(), handlers.ToArray());
    }

    /// <summary>Active actor activations across all shards (approximate, for metrics).</summary>
    public int ActorCount
    {
        get
        {
            int total = 0;
            foreach (var shard in _shards) total += shard.ActorCount;
            return total;
        }
    }

    /// <summary>Messages waiting in all shard mailboxes (approximate, for metrics and load reporting).</summary>
    public int TotalQueueLength()
    {
        int total = 0;
        foreach (var shard in _shards) total += shard.QueueLength;
        return total;
    }

    /// <summary>Calls and events refused by full shard inboxes or actor mailboxes since start (backpressure).</summary>
    public long Rejected
    {
        get
        {
            long total = 0;
            foreach (var shard in _shards) total += shard.Rejected;
            return total;
        }
    }

    private ulong[] _gateways = [];

    /// <summary>
    /// Called by membership when the view changes: shards drop actors they no longer own, and actors that knew sessions
    /// of a gateway that died (it could not close them) receive <see cref="Rpc.SessionClosed"/> for them.
    /// </summary>
    internal void OnViewChanged()
    {
        foreach (var shard in _shards) shard.RequestRebalance();

        var gateways = new List<ulong>();
        foreach (var member in View.Members)
            if (member.ClientPort != 0 && member.Status != MemberStatus.Dead) gateways.Add(member.NodeId);
        foreach (ulong previous in _gateways)
        {
            if (gateways.Contains(previous)) continue;
            Logger.LogWarning("Gateway {Gateway:x} is gone: closing its sessions", previous);
            foreach (var shard in _shards)
                shard.Post(static s =>
                {
                    var (target, gateway) = ((Shard, ulong))s!;
                    ShardDispatch.CloseSessionsOf(target.World, gateway);
                }, (shard, previous));
        }

        _gateways = gateways.ToArray();
    }

    private void Maintain()
    {
        try
        {
            Pending.SweepExpired();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Maintenance failed");
        }
    }
}

/// <summary>Ownership checks used by shards (thin wrapper over the current <see cref="ClusterView"/>).</summary>
internal sealed class NodePlacement(Node node)
{
    public bool IsLocalOwner(RpcContractInfo contract, long key) => node.View.ActorOwner(contract.Id, key)?.NodeId == node.Id;

    public bool IsLocalOwner(uint implementationId, long key) =>
        node.View.ImplementationOwner(implementationId, key)?.NodeId == node.Id;
}
