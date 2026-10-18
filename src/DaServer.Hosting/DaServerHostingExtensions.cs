using DaServer.Features;
using DaServer.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;

namespace DaServer.Hosting;

public static class DaServerHostingExtensions
{
    /// <summary>What the host and the platform add to <see cref="NodeOptions.ShutdownTimeout"/> before cutting a node off.</summary>
    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(5);

    /// <summary>Tag of the liveness check (<c>/alive</c>): a node that fails it is restarted, not just taken out of rotation.</summary>
    public const string LiveTag = "live";

    /// <summary>
    /// Run a DaServer <see cref="Node"/> in this host. Options bind from the <paramref name="section"/> configuration
    /// section (appsettings, environment variables such as <c>DaServer__Clients__Port=7100</c>, command line). Features
    /// run when their section is configured: client connections with <c>{section}:Clients</c>.
    /// The node starts with the host and leaves the cluster gracefully on SIGTERM / Ctrl+C. A stats line is logged every
    /// <c>{section}:StatsInterval</c> (default 30 s, 0 turns it off).
    /// </summary>
    public static IHostApplicationBuilder AddDaServer(this IHostApplicationBuilder builder, string section = "DaServer")
    {
        if (builder.Configuration[$"{section}:ListRoutes"] is { Length: > 0 } routes) ListRoutesAndExit(routes);
        // Defaults below every other source (appsettings, environment, command line override them): ASP.NET Core's
        // per-request information logs would drown a node's own logs under health probes. Hosting diagnostics off: any
        // level enabled there makes ASP.NET Core start an Activity per request (and fill its tags) whether or not
        // anything traces, a large share of a small request's cost.
        builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
        {
            InitialData =
            [
                new("Logging:LogLevel:Microsoft.AspNetCore", "Warning"),
                new("Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", "None"),
            ],
        });
        builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(section));
        builder.AddDaServerFeature<DaServer.Clients.ClientConnectionOptions>("Clients", static o => new DaServer.Clients.ClientConnections(o), section);
        builder.Services.AddSingleton(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            options.LoggerFactory ??= sp.GetRequiredService<ILoggerFactory>();
            return new Node(options);
        });
        builder.Services.AddSingleton<ICluster>(static sp => sp.GetRequiredService<Node>());
        builder.Services.AddSingleton<IMessageSender>(static sp => sp.GetRequiredService<Node>());
        builder.Services.AddHostedService<NodeHostedService>();
        // The host waits a little longer than the node's own stop budget, so the node reports instead of being cut off.
        builder.Services.AddOptions<HostOptions>().Configure<IOptions<NodeOptions>>(static (host, node) =>
            host.ShutdownTimeout = node.Value.ShutdownTimeout + ShutdownMargin);
        builder.Services.AddHealthChecks()
            .AddCheck<NodeReadiness>("daserver-ready")
            .AddCheck<NodeLiveness>("daserver-live", tags: [LiveTag]);
        var statsInterval = builder.Configuration.GetValue($"{section}:StatsInterval", TimeSpan.FromSeconds(30));
        if (statsInterval > TimeSpan.Zero)
            builder.Services.AddHostedService(sp => new NodeStatsLogger(sp.GetRequiredService<Node>(),
                sp.GetRequiredService<ILogger<NodeStatsLogger>>(), statsInterval));
        builder.AddDaServerTelemetry();
        return builder;
    }

    /// <summary>
    /// Check mode (<c>{section}:ListRoutes</c> = a file, set by the AppHost's route check before it publishes, A11): write
    /// the routes of this node project (the entry assembly: what its nodes host) to the file and exit, before anything starts.
    /// </summary>
    private static void ListRoutesAndExit(string file)
    {
        File.WriteAllLines(file, Routes.Of(System.Reflection.Assembly.GetEntryAssembly()!));
        Environment.Exit(0);
    }

    /// <summary>
    /// Run a feature (<see cref="NodeFeature"/>) when its section <c>{section}:{name}</c> is configured: its options bind
    /// from it (<c>DaServer__Clients__Port=7100</c> enables client connections). Built-in features are added by
    /// <see cref="AddDaServer"/>; call this for your own.
    /// </summary>
    public static IHostApplicationBuilder AddDaServerFeature<TOptions>(this IHostApplicationBuilder builder, string name,
        Func<TOptions, NodeFeature> create, string section = "DaServer") where TOptions : class, new()
    {
        var configuration = builder.Configuration.GetSection($"{section}:{name}");
        if (configuration.Exists())
            builder.Services.Configure<NodeOptions>(o => o.Features.Add(create(configuration.Get<TOptions>() ?? new TOptions())));
        return builder;
    }

    /// <summary>
    /// Metrics (DaServer meter, .NET runtime) for a Prometheus scrape of <c>/metrics</c> on the HTTP plane, always (what
    /// an HPA through the Prometheus adapter or KEDA reads); and logs and metrics over OTLP when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, which Aspire does for every resource (the dashboard shows them).
    /// Observable instruments are read only when collected: an unscraped node pays nothing.
    /// </summary>
    private static void AddDaServerTelemetry(this IHostApplicationBuilder builder)
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(static metrics => metrics.AddMeter(NodeMetrics.MeterName).AddRuntimeInstrumentation().AddPrometheusExporter());
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) return;
        builder.Logging.AddOpenTelemetry(static logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        telemetry.UseOtlpExporter();
    }
}

/// <summary>Starts the node with the host and stops it gracefully (Leaving → drain → unload) on shutdown.</summary>
internal sealed class NodeHostedService(Node node, IHostApplicationLifetime lifetime, System.Diagnostics.Metrics.IMeterFactory meters) : IHostedService
{
    private NodeMetrics? _metrics;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Downed on the minority side of a split: exit with an error so the platform restarts the node into the majority.
        node.Downed += () =>
        {
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        };
        await node.StartAsync(cancellationToken);
        _metrics = new NodeMetrics(node, meters); // after its features started: they have added their gauges
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await node.StopAsync(cancellationToken);
        _metrics?.Dispose(); // a stopped node is not sampled (nor kept alive) any more
        if (!node.StoppedClean) Environment.ExitCode = 1; // state was lost: not a clean exit for the platform
    }
}

/// <summary>Ready: joined, Up and not leaving (<c>/health</c>; platforms route traffic only to ready nodes).</summary>
internal sealed class NodeReadiness(Node node) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(node.IsReady ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("not joined, or leaving"));
}

/// <summary>Live: no shard thread stalled (<c>/alive</c>; a stalled node is restarted).</summary>
internal sealed class NodeLiveness(Node node) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(node.StalledShards == 0 ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"{node.StalledShards} shard(s) stalled longer than {node.Options.StallAfter}"));
}
