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
public sealed partial class Node : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PeerLink> _links = new();
    private Shard[] _shards = [];
    private FrozenDictionary<uint, ImplementationInfo> _implementationByContract = FrozenDictionary<uint, ImplementationInfo>.Empty;
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
        Logger.LogInformation("Node {Name} ({Id:x}) up on {Endpoint}, {Shards} shards, roles [{Roles}]",
            Options.Name, Id, ClusterEndPoint, shardCount, string.Join(",", Options.Roles));
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

    /// <summary>Hosted implementation serving <paramref name="contract"/> on this node, or null.</summary>
    internal ImplementationInfo? ImplementationOf(RpcContractInfo contract) =>
        _implementationByContract.TryGetValue(contract.Id, out var impl) ? impl : null;

    private void ResolveHosting()
    {
        var byContract = new Dictionary<uint, ImplementationInfo>();
        var hosted = new List<ImplementationInfo>();
        foreach (var impl in ServerRegistry.Implementations)
        {
            if (!ServerRegistry.IsHostedBy(impl.Roles, Options.Roles)) continue;
            hosted.Add(impl);
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
            if (ServerRegistry.IsHostedBy(system.Roles, Options.Roles))
                systems.Add(system);

        HostedImplementations = hosted.ToArray();
        HostedSystems = systems.ToArray();
        _implementationByContract = byContract.ToFrozenDictionary();
    }

    /// <summary>Contracts this node advertises in membership.</summary>
    internal (uint[] Services, HostedActor[] Actors) AdvertisedContracts()
    {
        var services = new List<uint>();
        var actors = new List<HostedActor>();
        foreach (var (contractId, impl) in _implementationByContract)
        {
            if (impl.Kind == RpcContractKind.Actor)
                actors.Add(new HostedActor { ContractId = contractId, ImplementationId = impl.Id });
            else
                services.Add(contractId);
        }

        return (services.ToArray(), actors.ToArray());
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

    /// <summary>Called by membership when the view changes: shards drop actors they no longer own.</summary>
    internal void OnViewChanged()
    {
        foreach (var shard in _shards) shard.RequestRebalance();
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
