using DaServer.Persistence;

namespace DaServer.Tests;

/// <summary>
/// Base class of the cluster tests that run against every store (<see cref="Tests.Stores"/>): one fixture per kind, whose
/// tables are dropped once it ends. Each derived fixture names its source:
/// <c>[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]</c>.
/// </summary>
public abstract class StoreTest(string kind) : ClusterTest
{
    /// <summary>Creates this fixture's stores (a new one per call: tests do not share records).</summary>
    private protected Stores Stores { get; } = new(kind);

    [OneTimeTearDown]
    public Task DropStores() => Stores.DisposeAsync().AsTask();

    /// <summary>A new store of this fixture's kind behind a <see cref="TestStore"/>.</summary>
    private protected TestStore NewStore() => new(Stores.Create());

    /// <summary>Start a node on <paramref name="store"/> (none: actors keep their state in memory only).</summary>
    private protected Task<Node> StartWith(IActorStore? store, string name = "single", Action<NodeOptions>? configure = null) =>
        Start(name, configure: o =>
        {
            o.ActorStore = store;
            configure?.Invoke(o);
        });
}
