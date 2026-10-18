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
        // Every wait below ends at once when a resource fails (a node that crashes, a database that exits): the watchdog
        // cancels it and the run fails naming that resource, instead of waiting for the wait's own limit.
        await using var watchdog = Watchdog.Start(app, load, cancellationToken);
        var token = watchdog.Token;
        using (var starting = CancellationTokenSource.CreateLinkedTokenSource(token))
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
                watchdog.ThrowIfFailed();
                Assert.Fail("The AppHost's resources did not start within 5 minutes (a container image not pulled?)");
            }
        }

        Phase("resources started");
        await EveryResourceHealthy(app, builder.Resources.Where(r => (r is ProjectResource && r.Name != load) || r is IResourceWithConnectionString),
            TimeSpan.FromMinutes(3), watchdog);
        await ClusterJoined(app, builder.Resources, watchdog);
        Phase("cluster healthy");

        try
        {
            var running = during?.Invoke(app, token) ?? Task.CompletedTask;
            var finished = await RunToEnd(app, load, token);
            Phase(load + " finished");
            await running;
            Assert.That(finished.Snapshot.ExitCode, Is.EqualTo(0), $"{load} ended {finished.Snapshot.State?.Text}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watchdog.ThrowIfFailed();
            throw;
        }

        // Stopping the AppHost stops the nodes first (DaServer.Aspire), so their last saves reach the database: a node
        // exits with 0 only when every actor was unloaded and saved (1 when state was lost, 137 when killed).
        watchdog.Stopping();
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
    private static async Task EveryResourceHealthy(DistributedApplication app, IEnumerable<IResource> resources, TimeSpan limit, Watchdog watchdog)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        timeout.CancelAfter(limit);
        foreach (var resource in resources)
        {
            try
            {
                await notifications.WaitForResourceHealthyAsync(resource.Name, timeout.Token);
            }
            catch (OperationCanceledException) when (!watchdog.Outer.IsCancellationRequested)
            {
                watchdog.ThrowIfFailed();
                string why = notifications.TryGetCurrentState(resource.Name, out var current)
                    ? $"{current.Snapshot.State?.Text}; " + string.Join("; ", current.Snapshot.HealthReports.Select(r => $"{r.Name}: {r.Status} {r.Description} {r.ExceptionText}"))
                    : "no state";
                Assert.Fail($"'{resource.Name}' was not healthy within {limit}: {why}");
            }
        }
    }

    /// <summary>
    /// The dashboard's view of the cluster (A5, A9): the cluster resource says every node joined, and a node's Membership
    /// command lists them all.
    /// </summary>
    private static async Task ClusterJoined(DistributedApplication app, IEnumerable<IResource> resources, Watchdog watchdog)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var nodes = resources.OfType<ProjectResource>().Where(static r => r.Annotations.OfType<ResourceCommandAnnotation>().Any(static c => c.Name == "membership")).ToList();
        var clusters = resources.Where(static r => r.GetType().Name == "DaServerClusterResource").ToList();
        Assert.That(clusters, Is.Not.Empty, "the AppHost declares its cluster with AddDaServerCluster");
        foreach (var cluster in clusters)
            await notifications.WaitForResourceAsync(cluster.Name, e => e.Snapshot.State?.Text == $"{nodes.Count}/{nodes.Count} joined", watchdog.Token)
                .WaitAsync(TimeSpan.FromMinutes(1), watchdog.Token);
        var membership = await app.Services.GetRequiredService<ResourceCommandService>().ExecuteCommandAsync(nodes[0].Name, "membership", watchdog.Token);
        Assert.That(membership.Success, Is.True, membership.Message);
        foreach (var node in nodes)
            Assert.That(membership.Data?.Value, Does.Contain($"\"name\":\"{node.Name}\""), "every node is a member");
    }

    /// <summary>
    /// Drain <paramref name="node"/> (its dashboard command: what a scale-in does, it leaves, hands its actors over and
    /// exits) and wait until it stopped; then start it again (a scale-out: it joins and takes its share) and wait until
    /// it is healthy.
    /// </summary>
    public static async Task Restart(DistributedApplication app, string node, TimeSpan downFor, CancellationToken cancellationToken)
    {
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        using var expected = Watchdog.Of(app)?.Expect(node); // its exit is the point, not a failure
        var drain = await commands.ExecuteCommandAsync(node, "drain", cancellationToken);
        Assert.That(drain.Success, Is.True, $"draining {node}: {drain.Message}");
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

/// <summary>
/// Watches every resource but the load while an AppHost runs: the first that fails to start, exits or turns unhealthy
/// at runtime cancels <see cref="Token"/>, and <see cref="ThrowIfFailed"/> fails the run naming it, with its exit code
/// and last log lines. A resource stopped on purpose (<see cref="Expect"/>) and the AppHost's own stop are not failures.
/// </summary>
internal sealed class Watchdog : IAsyncDisposable
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DistributedApplication, Watchdog> Running = new();
    private readonly CancellationTokenSource _failed;
    private readonly HashSet<string> _expected = [];
    private readonly Dictionary<string, Queue<string>> _tails = [];
    private Task _watch = Task.CompletedTask;
    private string? _reason;
    private volatile bool _stopping;

    private Watchdog(CancellationToken outer)
    {
        Outer = outer;
        _failed = CancellationTokenSource.CreateLinkedTokenSource(outer);
    }

    /// <summary>The test's own token.</summary>
    public CancellationToken Outer { get; }

    /// <summary>Cancelled at the first resource failure (or with the test's token).</summary>
    public CancellationToken Token => _failed.Token;

    public static Watchdog Start(DistributedApplication app, string load, CancellationToken cancellationToken)
    {
        var watchdog = new Watchdog(cancellationToken);
        Running.AddOrUpdate(app, watchdog);
        watchdog._watch = Task.Run(() => watchdog.Watch(app, load));
        return watchdog;
    }

    public static Watchdog? Of(DistributedApplication app) => Running.TryGetValue(app, out var watchdog) ? watchdog : null;

    /// <summary>Resource <paramref name="name"/> stops on purpose until the result is disposed.</summary>
    public IDisposable Expect(string name)
    {
        lock (_expected) _expected.Add(name);
        return new Expected(this, name);
    }

    /// <summary>The AppHost stops: resources exit from now on.</summary>
    public void Stopping() => _stopping = true;

    public void ThrowIfFailed()
    {
        if (_reason != null) Assert.Fail(_reason);
    }

    private async Task Watch(DistributedApplication app, string load)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        try
        {
            await foreach (var e in notifications.WatchAsync(_failed.Token))
            {
                string name = e.Resource.Name, state = e.Snapshot.State?.Text ?? "";
                if (name == load || _stopping) continue;
                if (!_tails.ContainsKey(name)) _ = Tail(logs, e.Resource);
                bool failed = state == KnownResourceStates.FailedToStart || state == KnownResourceStates.RuntimeUnhealthy ||
                              (state == KnownResourceStates.Exited || state == KnownResourceStates.Finished) && e.Snapshot.ExitCode is not null;
                bool expected;
                lock (_expected) expected = _expected.Contains(name);
                if (!failed || expected) continue;
                await Task.Delay(500); // its last lines
                string tail;
                lock (_tails) tail = _tails.TryGetValue(name, out var lines) ? string.Join(Environment.NewLine, lines) : "";
                _reason = $"'{name}' {state} (exit code {e.Snapshot.ExitCode?.ToString() ?? "none"}) while the run went on; its last lines:{Environment.NewLine}{tail}";
                await _failed.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The last 30 lines of a resource's output, for the failure message.
    private async Task Tail(ResourceLoggerService logs, IResource resource)
    {
        var lines = new Queue<string>();
        lock (_tails) _tails[resource.Name] = lines;
        try
        {
            await foreach (var batch in logs.WatchAsync(resource).WithCancellation(_failed.Token))
                lock (_tails)
                    foreach (var line in batch)
                    {
                        lines.Enqueue(line.Content);
                        if (lines.Count > 30) lines.Dequeue();
                    }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _failed.CancelAsync();
        try { await _watch; } catch (OperationCanceledException) { }
        _failed.Dispose();
    }

    private sealed class Expected(Watchdog watchdog, string name) : IDisposable
    {
        public void Dispose()
        {
            lock (watchdog._expected) watchdog._expected.Remove(name);
        }
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
