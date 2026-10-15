using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DaServer.E2E;

/// <summary>
/// The sample cluster as deployed: Aspire starts login, game, map and 3 client-facing gateway nodes, then the bots
/// connect through the gateways' addresses (the directory spreads them), log in, enter maps, move and receive the realm broadcasts. The bots' exit code is the verdict (no
/// gap, no disconnect, p99 under the limit).
/// </summary>
public class SampleClusterTests
{
    [Test, CancelAfter(240_000)]
    public async Task BotsPassAgainstTheSampleCluster(CancellationToken cancellationToken)
    {
        int bots = int.Parse(Environment.GetEnvironmentVariable("E2E_BOTS") ?? "1000");
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Sample_AppHost>(
            [$"Bots={bots}", "Seconds=15"], cancellationToken);
        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var finished = await notifications.WaitForResourceAsync("bots",
            e => e.Snapshot.State?.Text == KnownResourceStates.Finished || e.Snapshot.State?.Text == KnownResourceStates.Exited ||
                 e.Snapshot.State?.Text == KnownResourceStates.FailedToStart, cancellationToken);

        Assert.That(finished.Snapshot.ExitCode, Is.EqualTo(0), $"bots ended {finished.Snapshot.State?.Text}");
    }
}
