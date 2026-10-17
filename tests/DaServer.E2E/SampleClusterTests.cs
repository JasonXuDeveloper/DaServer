using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            [$"Bots={Get("BOTS", "1000")}", $"Seconds={Get("SECONDS", "10")}", $"Maps={Get("MAPS", "2")}",
                $"TravelEvery={Get("TRAVEL", "5")}", $"Lean={Get("LEAN", "false")}"], cancellationToken);

        AppHostRun.Prepare(builder);

        // Where the time goes, in the test log: start, cluster healthy, bots done, cluster stopped.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Phase(string what) => TestContext.Progress.WriteLine($"[e2e] {what} at {clock.Elapsed.TotalSeconds:F1} s");

        // What the AppHost reports about the nodes it stops first (DaServer.Aspire).
        var stops = new NodeStops();
        builder.Services.AddLogging(logging => logging.AddProvider(stops));

        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        Phase("resources started");
        await AppHostRun.EveryResourceHealthy(app, builder.Resources.Where(r => r is ProjectResource { Name: not "bots" } or IResourceWithConnectionString),
            TimeSpan.FromMinutes(3), cancellationToken);
        Phase("cluster healthy");

        var finished = await AppHostRun.RunToEnd(app, "bots", cancellationToken);
        Phase("bots finished");
        Assert.That(finished.Snapshot.ExitCode, Is.EqualTo(0), $"bots ended {finished.Snapshot.State?.Text}");

        // Stopping the AppHost stops the nodes first (DaServer.Aspire), so the game node's last saves reach the database:
        // a node exits with 0 only when every actor was unloaded and saved (1 when state was lost, 137 when killed).
        await app.StopAsync(cancellationToken);
        Phase("cluster stopped");
        Assert.That(stops.Lines, Has.Some.EqualTo("Node game-0 stopped with exit code 0"),
            "the game node saved every player before the database stopped");
        Assert.That(stops.Lines.Count(static l => l.EndsWith("exit code 0", StringComparison.Ordinal)), Is.EqualTo(6), string.Join("; ", stops.Lines));
    }
}
