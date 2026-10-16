using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DaServer.E2E;

/// <summary>
/// The sample cluster as deployed: Aspire starts login, game, map and 3 client-facing gateway nodes, then the bots
/// connect through the gateways' addresses (the directory spreads them), log in, change maps every 5 s, enter maps, move and receive the realm broadcasts. The bots' exit code is the verdict (no
/// gap, no disconnect, p99 under the limit).
/// </summary>
public class SampleClusterTests
{
    [Test, CancelAfter(600_000)]
    public async Task BotsPassAgainstTheSampleCluster(CancellationToken cancellationToken)
    {
        // CI: 1,000 full clients (keeping their view) in 2 maps, changing maps every 5 s. E2E_* variables run other loads
        // (the README's 10k scenario: E2E_BOTS=10000 E2E_MAPS=1 E2E_TRAVEL=0 E2E_SECONDS=60 E2E_LEAN=true).
        static string Get(string name, string fallback) => Environment.GetEnvironmentVariable("E2E_" + name) ?? fallback;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Sample_AppHost>(
            [$"Bots={Get("BOTS", "1000")}", $"Seconds={Get("SECONDS", "15")}", $"Maps={Get("MAPS", "2")}",
                $"TravelEvery={Get("TRAVEL", "5")}", $"Lean={Get("LEAN", "false")}"], cancellationToken);

        // The testing builder runs no dashboard but still points every resource at its OTLP endpoint: the nodes would
        // retry exports to nothing all run long and wait out the export timeout (10 s) on shutdown.
        foreach (var resource in builder.Resources.OfType<IResourceWithEnvironment>())
            resource.Annotations.Add(new EnvironmentCallbackAnnotation(static context =>
                context.EnvironmentVariables.Remove("OTEL_EXPORTER_OTLP_ENDPOINT")));

        // A fresh database per run: the sample keeps its actor store on a volume, initialized with the password of the
        // run that created it, while the testing builder generates a new one each time.
        foreach (var container in builder.Resources.OfType<ContainerResource>())
            foreach (var volume in container.Annotations.OfType<ContainerMountAnnotation>().ToList())
                container.Annotations.Remove(volume);

        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await EveryResourceHealthy(app, builder.Resources.Where(r => r is ProjectResource { Name: not "bots" } or IResourceWithConnectionString),
            TimeSpan.FromMinutes(3), cancellationToken);

        // The bots' output goes to the test log: a failure says why (gaps, disconnects, latency, failed map changes).
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        _ = Task.Run(async () =>
        {
            await foreach (var batch in logs.WatchAsync("bots").WithCancellation(cancellationToken))
                foreach (var line in batch)
                    TestContext.Progress.WriteLine(line.Content);
        }, cancellationToken);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var finished = await notifications.WaitForResourceAsync("bots",
            e => e.Snapshot.State?.Text == KnownResourceStates.Finished || e.Snapshot.State?.Text == KnownResourceStates.Exited ||
                 e.Snapshot.State?.Text == KnownResourceStates.FailedToStart, cancellationToken);

        Assert.That(finished.Snapshot.ExitCode, Is.EqualTo(0), $"bots ended {finished.Snapshot.State?.Text}");
    }

    /// <summary>
    /// Fail fast and say why when the cluster does not come up (a database refusing its password, a node that never
    /// joins): the bots would otherwise wait for it until the test's own timeout.
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
}
