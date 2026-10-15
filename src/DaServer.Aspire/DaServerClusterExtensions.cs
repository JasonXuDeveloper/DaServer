using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Declares a DaServer cluster in an Aspire AppHost. Every node gets a cluster endpoint, the seed list (every other
/// node) and, for gateways, a client endpoint; nodes discover each other through gossip. A node hosts every actor,
/// service and system compiled into its project, so the topology is the list of projects and replica counts. The same model runs
/// locally (<c>dotnet run</c> on the AppHost, with the dashboard) and publishes to Docker Compose or Kubernetes
/// (<c>aspire publish</c>) without hand-written deployment files.
/// </summary>
/// <example>
/// <code>
/// var cluster = builder.AddDaServerCluster("cluster");
/// cluster.AddNodes&lt;Projects.Sample_MapServer&gt;("map", replicas: 3);
/// cluster.AddGateways&lt;Projects.Sample_Gateway&gt;("gateway", replicas: 2);
/// var balancer = cluster.AddBalancer&lt;Projects.Sample_Gateway&gt;("balancer");
/// builder.AddProject&lt;Projects.Bot&gt;("bots").WithGateways(balancer);
/// </code>
/// </example>
public static class DaServerClusterExtensions
{
    /// <summary>
    /// Start a cluster declaration. Ports are assigned in order from <paramref name="clusterPortBase"/> (node links)
    /// and <paramref name="clientPortBase"/> (gateway client sockets), so a local run is predictable and every
    /// container in a published deployment listens on a known port.
    /// </summary>
    public static DaServerCluster AddDaServerCluster(this IDistributedApplicationBuilder builder, string name,
        int clusterPortBase = 7000, int clientPortBase = 7100) =>
        new(builder, name, clusterPortBase, clientPortBase);

    /// <summary>Pass the client endpoints of <paramref name="gateways"/> as <c>DaServer__Gateways__{i}</c> (host:port).</summary>
    public static IResourceBuilder<T> WithGateways<T>(this IResourceBuilder<T> resource, DaServerNodeGroup gateways)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        if (gateways.ClientEndpoints.Count == 0)
            throw new ArgumentException($"Node group '{gateways.Name}' has no client endpoints (declare it with AddGateways)");

        for (int i = 0; i < gateways.Nodes.Count; i++) resource.WaitFor(gateways.Nodes[i]);
        return resource.WithEnvironment(context =>
        {
            for (int i = 0; i < gateways.ClientEndpoints.Count; i++)
                context.EnvironmentVariables[$"DaServer__Gateways__{i}"] =
                    gateways.ClientEndpoints[i].Property(EndpointProperty.HostAndPort);
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
        // Signs client reconnect tickets: generated once and kept (user secrets locally, a deployment parameter when
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
    public DaServerNodeGroup AddNodes<TProject>(string name, int replicas = 1) where TProject : IProjectMetadata, new() =>
        Add<TProject>(name, replicas, gateway: false);

    /// <summary>
    /// Add <paramref name="replicas"/> nodes running <typeparamref name="TProject"/> that also accept clients (an external
    /// client endpoint each). Pass the group to <see cref="DaServerClusterExtensions.WithGateways{T}"/> of a client.
    /// </summary>
    public DaServerNodeGroup AddGateways<TProject>(string name, int replicas = 1) where TProject : IProjectMetadata, new() =>
        Add<TProject>(name, replicas, gateway: true);

    /// <summary>
    /// Add <paramref name="replicas"/> balancers: clients connect to their well-known address and are directed to
    /// the least loaded gateway; they keep no sessions themselves. Pass the group to
    /// <see cref="DaServerClusterExtensions.WithGateways{T}"/> of a client.
    /// </summary>
    public DaServerNodeGroup AddBalancer<TProject>(string name, int replicas = 1) where TProject : IProjectMetadata, new()
    {
        var group = Add<TProject>(name, replicas, gateway: true);
        foreach (var node in group.Nodes) node.WithEnvironment("DaServer__Balancer", "true");
        return group;
    }

    private DaServerNodeGroup Add<TProject>(string name, int replicas, bool gateway) where TProject : IProjectMetadata, new()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicas, 1);
        var nodes = new List<IResourceBuilder<ProjectResource>>(replicas);
        var clientEndpoints = new List<EndpointReference>();

        for (int i = 0; i < replicas; i++)
        {
            string nodeName = $"{name}-{i}";
            var node = _builder.AddProject<TProject>(nodeName)
                .WithEnvironment("DaServer__Name", nodeName)
                .WithEnvironment("DaServer__ClusterSecret", _secret)
                .WithEndpoint(port: _nextClusterPort, targetPort: _nextClusterPort++, scheme: "tcp", name: "cluster",
                    env: "DaServer__ClusterPort", isProxied: false);
            var self = node.GetEndpoint("cluster");
            _clusterEndpoints.Add(self);

            if (gateway)
            {
                node.WithEndpoint(port: _nextClientPort, targetPort: _nextClientPort++, scheme: "tcp", name: "client",
                    env: "DaServer__ClientPort", isProxied: false, isExternal: true);
                clientEndpoints.Add(node.GetEndpoint("client"));
            }

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

        return new DaServerNodeGroup(name, nodes, clientEndpoints);
    }
}

/// <summary>Nodes added by one <see cref="DaServerCluster.AddNodes{TProject}"/> or <see cref="DaServerCluster.AddGateways{TProject}"/> call.</summary>
public sealed class DaServerNodeGroup(string name, IReadOnlyList<IResourceBuilder<ProjectResource>> nodes,
    IReadOnlyList<EndpointReference> clientEndpoints)
{
    public string Name { get; } = name;

    public IReadOnlyList<IResourceBuilder<ProjectResource>> Nodes { get; } = nodes;

    /// <summary>Client endpoints (gateway groups only).</summary>
    public IReadOnlyList<EndpointReference> ClientEndpoints { get; } = clientEndpoints;
}
