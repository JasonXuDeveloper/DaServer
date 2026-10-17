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
        var stops = await AppHostRun.RunAsync<Projects.Sample_AppHost>(
            [$"Bots={Get("BOTS", "1000")}", $"Seconds={Get("SECONDS", "10")}", $"Maps={Get("MAPS", "2")}",
                $"TravelEvery={Get("TRAVEL", "5")}", $"Lean={Get("LEAN", "false")}"], "bots", cancellationToken);
        Assert.That(stops, Has.Some.EqualTo("Node game-0 stopped with exit code 0"), "the game node saved every player before the database stopped");
        Assert.That(stops.Count(static l => l.EndsWith("exit code 0", StringComparison.Ordinal)), Is.EqualTo(6), string.Join("; ", stops));
    }

    /// <summary>
    /// Scaling a stateful group under load (S6): two game nodes; while the bots play, one leaves (its players hand over
    /// to the other: saved state, sessions kept) and joins again (it takes its share back). The bots fail on any gap,
    /// disconnect or p99 over the limit, so their exit code says the scale-in and scale-out cost the players nothing.
    /// </summary>
    [Test, CancelAfter(600_000)]
    public async Task AStatefulGroupScalesInAndOutUnderLoad(CancellationToken cancellationToken)
    {
        var stops = await AppHostRun.RunAsync<Projects.Sample_AppHost>(
            ["Bots=1000", "Seconds=30", "Maps=2", "TravelEvery=5", "Lean=false", "Games=2"], "bots", cancellationToken,
            static async (app, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct); // the bots are in the game
                await AppHostRun.Restart(app, "game-1", TimeSpan.FromSeconds(5), ct);
            });
        Assert.That(stops.Count(static l => l.EndsWith("exit code 0", StringComparison.Ordinal)), Is.EqualTo(7), string.Join("; ", stops));
    }
}
