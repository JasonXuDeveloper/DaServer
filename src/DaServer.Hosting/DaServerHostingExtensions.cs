using System;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Rpc;
using DaServer.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;

namespace DaServer.Hosting;

public static class DaServerHostingExtensions
{
    /// <summary>
    /// Run a DaServer <see cref="Node"/> in this host. Options bind from the <paramref name="section"/> configuration
    /// section (appsettings, environment variables such as <c>DaServer__ClientPort=7100</c>, command line).
    /// The node starts with the host and leaves the cluster gracefully on SIGTERM / Ctrl+C. A stats line is logged every
    /// <c>{section}:StatsInterval</c> (default 30 s, 0 turns it off).
    /// </summary>
    public static IHostApplicationBuilder AddDaServer(this IHostApplicationBuilder builder, string section = "DaServer")
    {
        builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(section));
        builder.Services.AddSingleton(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            options.LoggerFactory ??= sp.GetRequiredService<ILoggerFactory>();
            return new Node(options);
        });
        builder.Services.AddSingleton<ICluster>(static sp => sp.GetRequiredService<Node>());
        builder.Services.AddSingleton<IRpcInvoker>(static sp => sp.GetRequiredService<Node>());
        builder.Services.AddHostedService<NodeHostedService>();
        var statsInterval = builder.Configuration.GetValue($"{section}:StatsInterval", TimeSpan.FromSeconds(30));
        if (statsInterval > TimeSpan.Zero)
            builder.Services.AddHostedService(sp => new NodeStatsLogger(sp.GetRequiredService<Node>(),
                sp.GetRequiredService<ILogger<NodeStatsLogger>>(), statsInterval));
        builder.AddDaServerTelemetry();
        return builder;
    }

    /// <summary>
    /// Export logs and metrics (DaServer meter, .NET runtime) over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set,
    /// which Aspire does for every resource (the dashboard shows them). No exporter otherwise: nothing runs.
    /// </summary>
    private static void AddDaServerTelemetry(this IHostApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) return;
        builder.Logging.AddOpenTelemetry(static logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        builder.Services.AddOpenTelemetry()
            .WithMetrics(static metrics => metrics.AddMeter(NodeMetrics.MeterName).AddRuntimeInstrumentation())
            .UseOtlpExporter();
    }
}

/// <summary>Starts the node with the host and stops it gracefully (Leaving → drain → deactivate) on shutdown.</summary>
internal sealed class NodeHostedService(Node node) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        NodeMetrics.Track(node);
        return node.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => node.StopAsync();
}
