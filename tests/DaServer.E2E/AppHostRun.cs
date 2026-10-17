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
    /// <summary>
    /// Start <typeparamref name="TAppHost"/> with <paramref name="args"/>, wait until every resource but the load
    /// <paramref name="load"/> is healthy, assert the load exits with 0, stop the AppHost (nodes first) and return what it
    /// said about stopping the nodes (<c>Node x stopped with exit code n</c>). Phases are timed in the test log.
    /// </summary>
    /// <param name="during">What happens to the cluster while the load runs (nodes stopped and started: scaling under load).</param>
    public static async Task<string[]> RunAsync<TAppHost>(string[] args, string load, CancellationToken cancellationToken,
        Func<DistributedApplication, CancellationToken, Task>? during = null) where TAppHost : class
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<TAppHost>(args, cancellationToken);
        Prepare(builder);
        var stops = new NodeStops();
        builder.Services.AddLogging(logging => logging.AddProvider(stops));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string what) => TestContext.Progress.WriteLine($"[e2e] {what} at {clock.Elapsed.TotalSeconds:F1} s");

        await using var app = await builder.BuildAsync(cancellationToken);
        using (var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // A container image that cannot be pulled (a registry or DNS outage on the runner) would hold the start until
            // the test's own limit: fail early, saying so.
            starting.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await app.StartAsync(starting.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Assert.Fail("The AppHost's resources did not start within 5 minutes (a container image not pulled?)");
            }
        }

        Phase("resources started");
        await EveryResourceHealthy(app, builder.Resources.Where(r => (r is ProjectResource && r.Name != load) || r is IResourceWithConnectionString),
            TimeSpan.FromMinutes(3), cancellationToken);
        Phase("cluster healthy");

        var running = during?.Invoke(app, cancellationToken) ?? Task.CompletedTask;
        var finished = await RunToEnd(app, load, cancellationToken);
        Phase(load + " finished");
        await running;
        Assert.That(finished.Snapshot.ExitCode, Is.EqualTo(0), $"{load} ended {finished.Snapshot.State?.Text}");

        // Stopping the AppHost stops the nodes first (DaServer.Aspire), so their last saves reach the database: a node
        // exits with 0 only when every actor was unloaded and saved (1 when state was lost, 137 when killed).
        await app.StopAsync(cancellationToken);
        Phase("cluster stopped");
        return stops.Lines;
    }

    private static void Prepare(IDistributedApplicationTestingBuilder builder)
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
    private static async Task EveryResourceHealthy(DistributedApplication app, IEnumerable<IResource> resources, TimeSpan limit,
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

    /// <summary>
    /// Stop <paramref name="node"/> gracefully (what a scale-in does: leave, hand its actors over, exit) and wait until it
    /// stopped; then start it again (a scale-out: it joins and takes its share) and wait until it is healthy.
    /// </summary>
    public static async Task Restart(DistributedApplication app, string node, TimeSpan downFor, CancellationToken cancellationToken)
    {
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var stop = await commands.ExecuteCommandAsync(node, KnownResourceCommands.StopCommand, cancellationToken);
        Assert.That(stop.Success, Is.True, $"stopping {node}: {stop.Message}");
        await notifications.WaitForResourceAsync(node, e => e.Snapshot.State?.Text is "Exited" or "Finished", cancellationToken);
        TestContext.Progress.WriteLine($"[e2e] {node} left the cluster");
        await Task.Delay(downFor, cancellationToken);
        var start = await commands.ExecuteCommandAsync(node, KnownResourceCommands.StartCommand, cancellationToken);
        Assert.That(start.Success, Is.True, $"starting {node}: {start.Message}");
        await notifications.WaitForResourceHealthyAsync(node, cancellationToken);
        TestContext.Progress.WriteLine($"[e2e] {node} joined again");
    }

    /// <summary>Wait for the load resource <paramref name="name"/> to end, its output in the test log (a failure says why).</summary>
    private static async Task<ResourceEvent> RunToEnd(DistributedApplication app, string name, CancellationToken cancellationToken)
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
