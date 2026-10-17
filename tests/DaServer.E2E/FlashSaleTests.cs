using NUnit.Framework;

namespace DaServer.E2E;

/// <summary>
/// The flash sale as deployed: two sale nodes and PostgreSQL through Aspire, then the crowd over the client protocol.
/// The crowd's exit code is the verdict: exactly the stock sold, nothing left, every retry answered alike, every order
/// readable.
/// </summary>
public class FlashSaleTests
{
    [Test, CancelAfter(900_000)]
    public async Task TheCrowdBuysExactlyTheStock(CancellationToken cancellationToken)
    {
        // CI: 100,000 buyers for 100 units; E2E_SALE_BUYERS=1000000 runs the README's million.
        string buyers = Environment.GetEnvironmentVariable("E2E_SALE_BUYERS") ?? "100000";
        var stops = await AppHostRun.RunAsync<Projects.FlashSale_AppHost>([$"Buyers={buyers}"], "crowd", cancellationToken);
        Assert.That(stops.Count(static l => l.EndsWith("exit code 0", StringComparison.Ordinal)), Is.EqualTo(2),
            "both sale nodes wrote every record before stopping: " + string.Join("; ", stops));
    }
}
