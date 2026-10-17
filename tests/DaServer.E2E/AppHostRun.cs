using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DaServer.E2E;

/// <summary>What every end-to-end test does with an AppHost: start it quietly on fresh containers, wait, read the verdict.</summary>
internal static class AppHostRun
{
    public static void Prepare(IDistributedApplicationTestingBuilder builder)
    {
        // The testing builder runs no dashboard but still gives every resource the OTLP variables. The nodes would export
        // to nothing all run long, and the .NET 11 CLI behind `dotnet run` turns its own OTLP telemetry on whenever any
        // OTEL_EXPORTER_OTLP_* variable is present: each process would wait out the export timeout (10 s) when it exits.
        foreach (var resource in builder.Resources.OfType<IResourceWithEnvironment>())
            resource.Annotations.Add(new EnvironmentCallbackAnnotation(static context =>
            {
                foreach (var name in context.EnvironmentVariables.Keys.Where(static k => k.StartsWith("OTEL_", StringComparison.Ordinal)).ToList())
                    context.EnvironmentVariables.Remove(name);
                context.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            }));

        // A fresh database per run, gone with the test: an AppHost may keep its store in a persistent container on a
        // volume, initialized with the password of the run that created it, while the testing builder generates a new
        // one each time.
        foreach (var container in builder.Resources.OfType<ContainerResource>())
            foreach (var annotation in container.Annotations.Where(static a => a is ContainerMountAnnotation or ContainerLifetimeAnnotation).ToList())
                container.Annotations.Remove(annotation);
    }

    /// <summary>
    /// Fail fast and say why when the cluster does not come up (a database refusing its password, a node that never
    /// joins): the load would otherwise wait for it until the test's own timeout.
    /// </summary>
    public static async Task EveryResourceHealthy(DistributedApplication app, IEnumerable<IResource> resources, TimeSpan limit,
        CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        foreach (var resource in resources)
        {
            try
            {
                await notifications.WaitForResourceHealthyAsync(resource.Name, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                string why = notifications.TryGetCurrentState(resource.Name, out var current)
                    ? $"{current.Snapshot.State?.Text}; " + string.Join("; ", current.Snapshot.HealthReports.Select(r => $"{r.Name}: {r.Status} {r.Description} {r.ExceptionText}"))
                    : "no state";
                Assert.Fail($"'{resource.Name}' was not healthy within {limit}: {why}");
            }
        }
    }

    /// <summary>Wait for the load resource <paramref name="name"/> to end, its output in the test log (a failure says why).</summary>
    public static async Task<ResourceEvent> RunToEnd(DistributedApplication app, string name, CancellationToken cancellationToken)
    {
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        _ = Task.Run(async () =>
        {
            await foreach (var batch in logs.WatchAsync(name).WithCancellation(cancellationToken))
                foreach (var line in batch)
                    TestContext.Progress.WriteLine(line.Content);
        }, cancellationToken);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        return await notifications.WaitForResourceAsync(name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Finished || e.Snapshot.State?.Text == KnownResourceStates.Exited ||
                 e.Snapshot.State?.Text == KnownResourceStates.FailedToStart, cancellationToken);
    }
}

/// <summary>The AppHost's lines about the nodes it stops first (DaServer.Aspire).</summary>
internal sealed class NodeStops : ILoggerProvider, ILogger
{
    private readonly List<string> _lines = [];

    public string[] Lines
    {
        get
        {
            lock (_lines) return _lines.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => categoryName.EndsWith("NodesStopFirst", StringComparison.Ordinal) ? this : NullLogger.Instance;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Dispose() { }
}
