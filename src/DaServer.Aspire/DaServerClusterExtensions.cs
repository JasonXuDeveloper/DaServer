#pragma warning disable ASPIREPROBES001 // HTTP probes: experimental in Aspire 13.6, what Kubernetes publishing needs
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Declares a DaServer cluster in an Aspire AppHost. Every node gets a cluster endpoint and seeds (run: every other node;
/// published: each group's DNS name), client-facing node groups a client endpoint (<see cref="DaServerNodeGroup.WithClientConnections"/>);
/// nodes discover each other through gossip. A node hosts every actor,
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
    /// entry addresses a client connects through. The client starts once every node of the cluster declared so far is
    /// ready, not only the client-facing ones: a client that logs in before the nodes hosting its actors are up fails.
    /// </summary>
    public static IResourceBuilder<T> WithClientEndpoints<T>(this IResourceBuilder<T> resource, DaServerNodeGroup nodes)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        if (nodes.ClientEndpoints.Count == 0)
            throw new ArgumentException($"Node group '{nodes.Name}' accepts no clients (call WithClientConnections on it)");

        foreach (var node in nodes.Cluster.Nodes) resource.WaitFor(node);
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
    private readonly List<IResourceBuilder<ProjectResource>> _nodes = [];
    private readonly List<DaServerNodeGroup> _groups = [];
    private readonly List<string> _peers = []; // published: the groups, each one DNS name for all its replicas
    private readonly int _clusterPortBase, _clientPortBase;
    private int _nextClusterPort;
    private int _nextClientPort;
    private bool _kubernetes; // published to Kubernetes: seeds are the groups' headless Services
    private readonly IResourceBuilder<DaServerClusterResource>? _resource; // run mode: the dashboard's parent of the nodes

    internal DaServerCluster(IDistributedApplicationBuilder builder, string name, int clusterPortBase, int clientPortBase)
    {
        _builder = builder;
        Name = name;
        // Signs client resume tickets: generated once and kept (user secrets locally, a deployment parameter when
        // published), the same for every node.
        _secret = builder.AddParameter($"{name}-secret", new GenerateParameterDefault { MinLength = 32, Special = false },
            secret: true, persist: true);
        _nextClusterPort = _clusterPortBase = clusterPortBase;
        _nextClientPort = _clientPortBase = clientPortBase;
        if (builder.ExecutionContext.IsPublishMode)
        {
            RouteCheckStep.Add(builder, this);
            builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
            {
                foreach (var compose in e.Model.Resources.OfType<Docker.DockerComposeEnvironmentResource>())
                    builder.CreateResourceBuilder(compose).ConfigureComposeFile(WaitForHealthyNodes);
                return Task.CompletedTask;
            });
        }
        if (builder.ExecutionContext.IsRunMode)
        {
            // The dashboard shows the cluster as a resource of its own, its nodes under it (A5).
            _resource = builder.AddResource(new DaServerClusterResource(name)).ExcludeFromManifest()
                .WithInitialState(new CustomResourceSnapshot { ResourceType = "DaServer cluster", State = "Starting", Properties = [] });
            builder.Services.AddHostedService(sp => new ClusterStatus(this, _resource.Resource, sp.GetRequiredService<ResourceNotificationService>()));
            builder.Services.AddHostedService(sp => new NodesStopFirst(this, sp.GetRequiredService<ResourceNotificationService>(), sp.GetRequiredService<ResourceCommandService>(),
                sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NodesStopFirst>>()));
        }
    }

    /// <summary>The longest stop budget of the cluster's node groups, plus the margin a node's host adds.</summary>
    internal TimeSpan StopBudget
    {
        get
        {
            var longest = TimeSpan.Zero;
            foreach (var group in _groups) if (group.StopGrace > longest) longest = group.StopGrace;
            return longest;
        }
    }

    public string Name { get; }

    /// <summary>Every node declared so far, of every group.</summary>
    public IReadOnlyList<IResourceBuilder<ProjectResource>> Nodes => _nodes;

    /// <summary>
    /// Add <paramref name="replicas"/> nodes running <typeparamref name="TProject"/>. Run locally, each replica is its own
    /// resource (<c>{name}-0</c>, <c>{name}-1</c>, ...) on ports of its own, seeded with every other node. Published, the
    /// group is one workload with <paramref name="replicas"/> replicas (a Compose service, a Kubernetes Deployment) that
    /// the platform scales: every replica listens on the same ports, advertises its own address (a pod's IP), is named
    /// after its host, and finds the others through the groups' DNS names (Compose service names, a headless Kubernetes
    /// Service per group, <c>{name}-peers</c>), so a replica added by the platform joins on its own.
    /// </summary>
    public DaServerNodeGroup AddNodes<TProject>(string name, int replicas = 1) where TProject : IProjectMetadata, new()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicas, 1);
        bool publish = _builder.ExecutionContext.IsPublishMode;
        var nodes = new List<IResourceBuilder<ProjectResource>>(publish ? 1 : replicas);
        if (publish)
        {
            var node = Node<TProject>(name).WithReplicas(replicas)
                .WithEndpoint(targetPort: _clusterPortBase, scheme: "tcp", name: "cluster", env: "DaServer__ClusterPort", isProxied: false);
            _peers.Add(name);
            node.WithEnvironment(context => // seeds: every group's DNS name, this one's included (a node skips itself)
            {
                for (int seed = 0; seed < _peers.Count; seed++)
                    context.EnvironmentVariables[$"DaServer__Seeds__{seed}"] = $"{PeersHost(_peers[seed])}:{_clusterPortBase}";
            });
            nodes.Add(node);
        }
        else
            for (int i = 0; i < replicas; i++)
            {
                string nodeName = $"{name}-{i}";
                var node = Node<TProject>(nodeName)
                    .WithEnvironment("DaServer__Name", nodeName)
                    .WithEndpoint(port: _nextClusterPort, targetPort: _nextClusterPort++, scheme: "tcp", name: "cluster",
                        env: "DaServer__ClusterPort", isProxied: false);
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

        _nodes.AddRange(nodes);
        var group = new DaServerNodeGroup(this, name, nodes);
        _groups.Add(group);
        // What the platform needs to know of a node, for the environments the model publishes to (each hook requires its
        // environment, which may be added after the nodes).
        _builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
        {
            int grace = (int)Math.Ceiling(group.StopGrace.TotalSeconds);
            bool compose = e.Model.Resources.OfType<Docker.DockerComposeEnvironmentResource>().Any();
            bool kubernetes = e.Model.Resources.OfType<Kubernetes.KubernetesEnvironmentResource>().Any();
            _kubernetes |= kubernetes && !compose;
            foreach (var node in nodes)
            {
                // Compose stops a container after 10 s by default, Kubernetes kills a pod after 30 s.
                if (compose)
                    node.PublishAsDockerComposeService((_, service) => Compose(service, node.Resource, publish ? replicas : 1, grace));
                if (kubernetes)
                    node.PublishAsKubernetesService(resource => Kubernetes(resource, name, replicas, grace));
            }

            return Task.CompletedTask;
        });
        return group;
    }

    /// <summary>A node resource: the cluster's name and secret, the HTTP plane with its probes.</summary>
    private IResourceBuilder<ProjectResource> Node<TProject>(string name) where TProject : IProjectMetadata, new()
    {
        var node = _builder.AddProject<TProject>(name)
            .WithEnvironment("DaServer__ClusterName", Name)
            .WithEnvironment("DaServer__SigningSecret", _secret)
            // The HTTP plane (ADR-046): health for WaitFor and the dashboard, probes for Kubernetes. Ready = joined and not
            // leaving (traffic and dependants wait for it); alive = no stalled shard (restart otherwise).
            .WithHttpEndpoint(name: "http", env: "DaServer__HttpPort")
            .WithHttpProbe(ProbeType.Readiness, "/health", endpointName: "http")
            .WithHttpProbe(ProbeType.Liveness, "/alive", initialDelaySeconds: 10, endpointName: "http");
        NodeCommands.Add(node, _secret);
        if (_resource != null) node.WithParentRelationship(_resource);
        return node;
    }

    /// <summary>
    /// A group's Compose service: the grace period, a restart policy, a health check, and its replicas, which share the
    /// container ports (several replicas cannot share a host port: the host maps theirs, or a proxy in front of them does).
    /// The health check is the node's readiness (<c>/health</c>: joined, not leaving), asked by the node's own executable
    /// in probe mode (the .NET images ship no curl or wget; a .NET project's image runs <c>dotnet /app/{assembly}.dll</c>), so
    /// services waiting for the node (<c>WaitFor</c>) start once it joined.
    /// </summary>
    private static void Compose(Docker.Resources.ComposeNodes.Service service, ProjectResource node, int replicas, int grace)
    {
        service.StopGracePeriod = $"{grace}s";
        service.Restart = "unless-stopped";
        string assembly = Path.GetFileNameWithoutExtension(node.GetProjectMetadata().ProjectPath);
        service.Healthcheck = new()
        {
            Test = ["CMD", "dotnet", $"/app/{assembly}.dll", "--DaServer:Probe=/health"],
            Interval = "10s", Timeout = "5s", Retries = 3, StartPeriod = "60s",
        };
        if (replicas == 1) return;
        (service.Deploy ??= new()).Replicas = replicas;
        for (int i = 0; i < service.Ports.Count; i++)
            if (service.Ports[i].LastIndexOf(':') is var colon and > 0) service.Ports[i] = service.Ports[i][(colon + 1)..];
    }

    /// <summary>
    /// Services waiting for a node group (<c>WaitFor</c>: clients, other nodes' dependants) start once its health check
    /// passes, that is once a node joined, not as soon as its container started (Compose's default condition).
    /// </summary>
    private void WaitForHealthyNodes(Docker.Resources.ComposeFile file)
    {
        foreach (var service in file.Services.Values)
            if (service.DependsOn != null)
                foreach (var (name, dependency) in service.DependsOn)
                    if (_peers.Contains(name)) dependency.Condition = "service_healthy";
    }

    /// <summary>The DNS name resolving to every replica of group <paramref name="group"/>.</summary>
    private string PeersHost(string group) => _kubernetes ? $"{group}-peers" : group;

    /// <summary>
    /// A group's Kubernetes objects: the grace period; a preStop pause, so the Services stop routing to a stopping pod
    /// before it leaves; and a headless Service listing every pod, ready or not (a node joins before it is ready), whose
    /// name the seeds resolve.
    /// </summary>
    private void Kubernetes(Kubernetes.KubernetesResource resource, string group, int replicas, int grace)
    {
        if (resource.Workload is Kubernetes.Resources.Deployment deployment) deployment.Spec.Replicas = replicas;
        if (resource.Workload?.PodTemplate.Spec is not { } pod) return;
        pod.TerminationGracePeriodSeconds = grace + PreStopSeconds; // the pause counts against the grace period
        foreach (var container in pod.Containers) // one action: the other handler kinds stay unset
            (container.Lifecycle ??= new()).PreStop = new()
                { Sleep = new() { Seconds = PreStopSeconds }, Exec = null!, HttpGet = null!, TcpSocket = null! };
        var peers = new Kubernetes.Resources.Service();
        peers.Metadata.Name = $"{group}-peers";
        peers.Spec.ClusterIp = "None";
        peers.Spec.PublishNotReadyAddresses = true;
        if (resource.Service is { } service)
            foreach (var (key, value) in service.Spec.Selector) peers.Spec.Selector[key] = value;
        peers.Spec.Ports.Add(new() { Name = "cluster", Port = _clusterPortBase, TargetPort = _clusterPortBase });
        resource.AdditionalResources.Add(peers);
    }

    /// <summary>
    /// How long a stopping pod keeps serving before it is told to stop: the time Services and load balancers take to stop
    /// routing to it (they learn of the stop at the same time as the pod).
    /// </summary>
    internal const int PreStopSeconds = 5;

    /// <summary>
    /// What the platform waits beyond a node's stop budget before killing it: the host's own margin
    /// (<c>DaServerHostingExtensions.ShutdownMargin</c>, 5 s: the host gives up at budget + 5 s; the AppHost does not
    /// reference the node's packages) and 5 s more, so the kill never lands while the host is still stopping.
    /// </summary>
    internal static readonly TimeSpan StopMargin = TimeSpan.FromSeconds(10);

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

    /// <summary>The cluster the group belongs to.</summary>
    public DaServerCluster Cluster => _cluster;

    public IReadOnlyList<IResourceBuilder<ProjectResource>> Nodes { get; }

    /// <summary>The nodes' stop budget (<see cref="WithShutdownTimeout"/>; 25 s, the node's default, <c>NodeOptions.ShutdownTimeout</c>).</summary>
    public TimeSpan ShutdownTimeout { get; private set; } = TimeSpan.FromSeconds(25);

    /// <summary>How long the platform waits for a node of this group before killing it: the stop budget and a margin.</summary>
    internal TimeSpan StopGrace => ShutdownTimeout + DaServerCluster.StopMargin;

    /// <summary>
    /// Give the nodes <paramref name="budget"/> to stop gracefully (leave, drain clients, unload and save every actor):
    /// sets <c>DaServer__ShutdownTimeout</c>, and the published grace period (Compose <c>stop_grace_period</c>, Kubernetes
    /// <c>terminationGracePeriodSeconds</c> with the preStop pause) above it,
    /// so the platform never kills a node that is still saving. Stateful groups need more than stateless ones.
    /// </summary>
    public DaServerNodeGroup WithShutdownTimeout(TimeSpan budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        ShutdownTimeout = budget;
        foreach (var node in Nodes) node.WithEnvironment("DaServer__ShutdownTimeout", budget.ToString());
        return this;
    }

    /// <summary>
    /// Set <paramref name="name"/> on every node of the group (a node option such as <c>DaServer__Shards</c>, or the
    /// application's own). A parameter (<c>builder.AddParameter</c>) is asked for at run, and published as a Compose
    /// variable or a Helm value, so each environment sets it at deploy time.
    /// </summary>
    public DaServerNodeGroup WithEnvironment(string name, IResourceBuilder<ParameterResource> value)
    {
        foreach (var node in Nodes) node.WithEnvironment(name, value);
        return this;
    }

    /// <summary>Set <paramref name="name"/> to <paramref name="value"/> on every node of the group.</summary>
    public DaServerNodeGroup WithEnvironment(string name, string value)
    {
        foreach (var node in Nodes) node.WithEnvironment(name, value);
        return this;
    }

    /// <summary>
    /// The nodes keep their actor records (ADR-068) in <paramref name="store"/>, a database resource
    /// (<c>builder.AddPostgres("pg").AddDatabase("actors")</c>): its connection string as
    /// <c>ConnectionStrings__{connectionName}</c>, which <c>AddPostgreSqlActorStore(connectionName)</c> in the node's
    /// Program.cs reads. The nodes wait for it: a node prepares its store before it starts (ADR-068) and refuses to start
    /// without it, and a deployment starts the database first and stops it last, after the nodes' final saves.
    /// </summary>
    public DaServerNodeGroup WithActorStore(IResourceBuilder<IResourceWithConnectionString> store, string connectionName = "actors")
    {
        foreach (var node in Nodes) node.WithReference(store, connectionName).WaitFor(store);

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
            // Published, the replicas of a group share the port (each has its own address; the platform maps it).
            int port = _cluster.NextClientPort();
            node.WithEndpoint(port: node.ApplicationBuilder.ExecutionContext.IsPublishMode ? null : port, targetPort: port,
                scheme: "tcp", name: "client", env: "DaServer__Clients__Port", isProxied: false, isExternal: true);
            _clientEndpoints.Add(node.GetEndpoint("client"));
        }

        return this;
    }
}
