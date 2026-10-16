#pragma warning disable ASPIREPROBES001 // HTTP probes: experimental in Aspire 13.6, what Kubernetes publishing needs
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Declares a DaServer cluster in an Aspire AppHost. Every node gets a cluster endpoint and the seed list (every other
/// node), client-facing node groups a client endpoint each (<see cref="DaServerNodeGroup.WithClientConnections"/>); nodes
/// discover each other through gossip. A node hosts every actor,
/// service and system compiled into its project, so the topology is the list of projects and replica counts. The same model runs
/// locally (<c>dotnet run</c> on the AppHost, with the dashboard) and publishes to Docker Compose or Kubernetes
/// (<c>aspire publish</c>) without hand-written deployment files.
/// </summary>
/// <example>
/// <code>
/// var cluster = builder.AddDaServerCluster("cluster");
/// cluster.AddNodes&lt;Projects.Sample_MapServer&gt;("map", replicas: 3);
/// var gateways = cluster.AddNodes&lt;Projects.Sample_GatewayServer&gt;("gateway", replicas: 2).WithClientConnections();
/// builder.AddProject&lt;Projects.Bot&gt;("bots").WithClientEndpoints(gateways);
/// </code>
/// </example>
public static class DaServerClusterExtensions
{
    /// <summary>
    /// Start a cluster declaration. Ports are assigned in order from <paramref name="clusterPortBase"/> (node links)
    /// and <paramref name="clientPortBase"/> (client sockets of client-facing nodes), so a local run is predictable and every
    /// container in a published deployment listens on a known port.
    /// </summary>
    public static DaServerCluster AddDaServerCluster(this IDistributedApplicationBuilder builder, string name,
        int clusterPortBase = 7000, int clientPortBase = 7100) =>
        new(builder, name, clusterPortBase, clientPortBase);

    /// <summary>
    /// Pass the client endpoints of <paramref name="nodes"/> as <c>DaServer__ClientEndpoints__{i}</c> (host:port), the
    /// entry addresses a client connects through.
    /// </summary>
    public static IResourceBuilder<T> WithClientEndpoints<T>(this IResourceBuilder<T> resource, DaServerNodeGroup nodes)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        if (nodes.ClientEndpoints.Count == 0)
            throw new ArgumentException($"Node group '{nodes.Name}' accepts no clients (call WithClientConnections on it)");

        for (int i = 0; i < nodes.Nodes.Count; i++) resource.WaitFor(nodes.Nodes[i]);
        return resource.WithEnvironment(context =>
        {
            for (int i = 0; i < nodes.ClientEndpoints.Count; i++)
                context.EnvironmentVariables[$"DaServer__ClientEndpoints__{i}"] =
                    nodes.ClientEndpoints[i].Property(EndpointProperty.HostAndPort);
        });
    }
}

/// <summary>A cluster being declared: node groups share one seed list and one port plan.</summary>
public sealed class DaServerCluster
{
    private readonly IDistributedApplicationBuilder _builder;
    private readonly IResourceBuilder<ParameterResource> _secret;
    private readonly List<EndpointReference> _clusterEndpoints = [];
    private int _nextClusterPort;
    private int _nextClientPort;

    internal DaServerCluster(IDistributedApplicationBuilder builder, string name, int clusterPortBase, int clientPortBase)
    {
        _builder = builder;
        Name = name;
        // Signs client resume tickets: generated once and kept (user secrets locally, a deployment parameter when
        // published), the same for every node.
        _secret = builder.AddParameter($"{name}-secret", new GenerateParameterDefault { MinLength = 32, Special = false },
            secret: true, persist: true);
        _nextClusterPort = clusterPortBase;
        _nextClientPort = clientPortBase;
    }

    public string Name { get; }

    /// <summary>
    /// Add <paramref name="replicas"/> nodes running <typeparamref name="TProject"/>. Each replica is its own resource
    /// (<c>{name}-0</c>, <c>{name}-1</c>, ...) because cluster members are addressed individually, not through a load
    /// balancer.
    /// </summary>
    public DaServerNodeGroup AddNodes<TProject>(string name, int replicas = 1) where TProject : IProjectMetadata, new()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicas, 1);
        var nodes = new List<IResourceBuilder<ProjectResource>>(replicas);

        for (int i = 0; i < replicas; i++)
        {
            string nodeName = $"{name}-{i}";
            var node = _builder.AddProject<TProject>(nodeName)
                .WithEnvironment("DaServer__Name", nodeName)
                .WithEnvironment("DaServer__SigningSecret", _secret)
                .WithEndpoint(port: _nextClusterPort, targetPort: _nextClusterPort++, scheme: "tcp", name: "cluster",
                    env: "DaServer__ClusterPort", isProxied: false)
                // The HTTP plane (ADR-046): health for WaitFor and the dashboard, probes for Kubernetes. Ready = joined
                // and not leaving (traffic and dependants wait for it); alive = no stalled shard (restart otherwise).
                .WithHttpEndpoint(name: "http", env: "DaServer__HttpPort")
                .WithHttpProbe(ProbeType.Readiness, "/health", endpointName: "http")
                .WithHttpProbe(ProbeType.Liveness, "/alive", initialDelaySeconds: 10, endpointName: "http");
            var self = node.GetEndpoint("cluster");
            _clusterEndpoints.Add(self);

            // Evaluated when the node starts, so it sees nodes declared after it: seeds are every other member.
            node.WithEnvironment(context =>
            {
                context.EnvironmentVariables["DaServer__AdvertisedHost"] = self.Property(EndpointProperty.Host);
                int seed = 0;
                foreach (var other in _clusterEndpoints)
                    if (!ReferenceEquals(other, self))
                        context.EnvironmentVariables[$"DaServer__Seeds__{seed++}"] = other.Property(EndpointProperty.HostAndPort);
            });
            nodes.Add(node);
        }

        var group = new DaServerNodeGroup(this, name, nodes);
        foreach (var node in nodes)
            node.PublishAsDockerComposeService((_, service) =>
            {
                // Compose stops a container after 10 s by default: give the node its whole stop budget plus a margin.
                service.StopGracePeriod = $"{(int)Math.Ceiling((group.ShutdownTimeout + StopMargin).TotalSeconds)}s";
                service.Restart = "unless-stopped";
            });
        return group;
    }

    /// <summary>What the platform waits beyond a node's stop budget before killing it (the host's own margin is 5 s).</summary>
    private static readonly TimeSpan StopMargin = TimeSpan.FromSeconds(5);

    /// <summary>The next client port (client-facing node groups take one per node, in declaration order).</summary>
    internal int NextClientPort() => _nextClientPort++;
}

/// <summary>Nodes added by one <see cref="DaServerCluster.AddNodes{TProject}"/> call.</summary>
public sealed class DaServerNodeGroup
{
    private readonly DaServerCluster _cluster;
    private readonly List<EndpointReference> _clientEndpoints = [];

    internal DaServerNodeGroup(DaServerCluster cluster, string name, IReadOnlyList<IResourceBuilder<ProjectResource>> nodes)
    {
        _cluster = cluster;
        Name = name;
        Nodes = nodes;
    }

    public string Name { get; }

    public IReadOnlyList<IResourceBuilder<ProjectResource>> Nodes { get; }

    /// <summary>The nodes' stop budget (<see cref="WithShutdownTimeout"/>; 25 s, the node's default).</summary>
    public TimeSpan ShutdownTimeout { get; private set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Give the nodes <paramref name="budget"/> to stop gracefully (leave, drain clients, unload and save every actor):
    /// sets <c>DaServer__ShutdownTimeout</c>, and the published grace period (Compose <c>stop_grace_period</c>) above it,
    /// so the platform never kills a node that is still saving. Stateful groups need more than stateless ones.
    /// </summary>
    public DaServerNodeGroup WithShutdownTimeout(TimeSpan budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        ShutdownTimeout = budget;
        foreach (var node in Nodes) node.WithEnvironment("DaServer__ShutdownTimeout", budget.ToString());
        return this;
    }

    /// <summary>Client endpoints (after <see cref="WithClientConnections"/>).</summary>
    public IReadOnlyList<EndpointReference> ClientEndpoints => _clientEndpoints;

    /// <summary>
    /// The nodes accept clients: an external client endpoint each (<c>DaServer__Clients__Port</c>, which enables the
    /// client connections feature). Pass the group to
    /// <see cref="DaServerClusterExtensions.WithClientEndpoints{T}"/> of a client.
    /// </summary>
    public DaServerNodeGroup WithClientConnections()
    {
        if (_clientEndpoints.Count != 0) return this;
        foreach (var node in Nodes)
        {
            int port = _cluster.NextClientPort();
            node.WithEndpoint(port: port, targetPort: port, scheme: "tcp", name: "client",
                env: "DaServer__Clients__Port", isProxied: false, isExternal: true);
            _clientEndpoints.Add(node.GetEndpoint("client"));
        }

        return this;
    }
}
