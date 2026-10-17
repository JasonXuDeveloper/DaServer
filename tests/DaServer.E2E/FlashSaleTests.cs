using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace DaServer.E2E;

/// <summary>
/// The flash sale as deployed: two sale nodes and PostgreSQL through Aspire, then the crowd over HTTP. The crowd's
/// exit code is the verdict: exactly the stock sold, nothing left, every retry answered alike, every order readable.
/// </summary>
public class FlashSaleTests
{
    [Test, CancelAfter(900_000)]
    public async Task TheCrowdBuysExactlyTheStock(CancellationToken cancellationToken)
    {
        // CI: 100,000 buyers for 100 units; E2E_SALE_USERS=1000000 runs the README's million.
        string users = Environment.GetEnvironmentVariable("E2E_SALE_USERS") ?? "100000";
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FlashSale_AppHost>([$"Users={users}"], cancellationToken);
        AppHostRun.Prepare(builder);
        var stops = new NodeStops();
        builder.Services.AddLogging(logging => logging.AddProvider(stops));

        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await AppHostRun.EveryResourceHealthy(app, builder.Resources.Where(r => r is ProjectResource { Name: not "crowd" } or IResourceWithConnectionString),
            TimeSpan.FromMinutes(3), cancellationToken);
        var finished = await AppHostRun.RunToEnd(app, "crowd", cancellationToken);
        Assert.That(finished.Snapshot.ExitCode, Is.EqualTo(0), $"the crowd ended {finished.Snapshot.State?.Text}");

        await app.StopAsync(cancellationToken);
        Assert.That(stops.Lines.Count(static l => l.EndsWith("exit code 0", StringComparison.Ordinal)), Is.EqualTo(2),
            "both sale nodes wrote every record before stopping: " + string.Join("; ", stops.Lines));
    }
}
