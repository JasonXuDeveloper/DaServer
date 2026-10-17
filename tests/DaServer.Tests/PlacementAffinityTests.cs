namespace DaServer.Tests;

/// <summary>IPlaced id types (N7, ADR-073): actors with the same placement live on one node and one shard.</summary>
public class PlacementAffinityTests : ClusterTest
{
    [Test]
    public async Task ActorsWithTheSamePlacementShareANodeAndAShard()
    {
        var nodes = await StartCluster(3, shards: 4);
        var tables = new HashSet<string>();
        for (long table = 1; table <= 30; table++)
        {
            string at = await nodes[0].Ask(new TableId(table), new WhereIsIt());
            tables.Add(at);
            for (int chair = 0; chair < 4; chair++)
                Assert.That(await nodes[chair % 3].Ask(ChairId.Of(table, chair), new WhereIsIt()), Is.EqualTo(at),
                    $"chair {chair} of table {table} runs on its table's shard thread");
        }

        Assert.That(tables.Count, Is.GreaterThan(3), "tables still spread over nodes and shards");
    }
}
