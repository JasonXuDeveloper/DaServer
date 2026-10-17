using DaServer.Persistence;
using Npgsql;

namespace DaServer.Tests;

/// <summary>
/// The stores under test: in memory always; PostgreSQL when <c>DASERVER_TEST_POSTGRES</c> holds a connection string
/// (CI runs a PostgreSQL service). Each store gets its own table, dropped when the fixture ends.
/// </summary>
public sealed class Stores(string kind) : IAsyncDisposable
{
    public const string Memory = "memory", PostgreSql = "postgresql";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("DASERVER_TEST_POSTGRES");
    private readonly List<string> _tables = [];

    public static IEnumerable<string> Kinds() => [Memory, PostgreSql];

    /// <summary>Close every other connection to the test database on the server's side (a restart, a failover).</summary>
    public static async Task DropConnections()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var kill = dataSource.CreateCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()");
        await kill.ExecuteNonQueryAsync();
    }

    public IActorStore Create()
    {
        if (kind == Memory) return new InMemoryActorStore();
        if (string.IsNullOrEmpty(ConnectionString)) Assert.Ignore("DASERVER_TEST_POSTGRES is not set");
        string table = "actors_" + Guid.NewGuid().ToString("N");
        _tables.Add(table);
        return new PostgreSqlActorStore(ConnectionString!, table);
    }

    /// <summary><paramref name="count"/> stores on one new table (nodes of one cluster starting together).</summary>
    public IActorStore[] CreateShared(int count)
    {
        if (kind == Memory)
        {
            var store = new InMemoryActorStore();
            return [.. Enumerable.Repeat<IActorStore>(store, count)];
        }

        if (string.IsNullOrEmpty(ConnectionString)) Assert.Ignore("DASERVER_TEST_POSTGRES is not set");
        string table = "actors_" + Guid.NewGuid().ToString("N");
        _tables.Add(table);
        return [.. Enumerable.Range(0, count).Select(_ => (IActorStore)new PostgreSqlActorStore(ConnectionString!, table))];
    }

    public async ValueTask DisposeAsync()
    {
        if (_tables.Count == 0) return;
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        foreach (var table in _tables)
        {
            await using var drop = dataSource.CreateCommand($"DROP TABLE IF EXISTS {table}");
            await drop.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>What every <see cref="IActorStore"/> guarantees: batched loads, versioned writes that fence stale owners.</summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class ActorStoreTests(string kind)
{
    private readonly Stores _stores = new(kind);

    [OneTimeTearDown]
    public Task DropStores() => _stores.DisposeAsync().AsTask();

    private static readonly StoredKey A = new(7, 1), B = new(7, 2), C = new(unchecked((uint)-5), long.MaxValue);

    internal static async Task<StoredRecord[]> Load(IActorStore store, params StoredKey[] keys)
    {
        var records = new StoredRecord[keys.Length];
        await store.LoadAsync(keys, records);
        return records;
    }

    internal static async Task<bool[]> Save(IActorStore store, params StoredWrite[] writes)
    {
        var accepted = new bool[writes.Length];
        await store.SaveAsync(writes, accepted);
        return accepted;
    }

    [Test]
    public async Task NodesStartingTogetherCreateTheTableOnce()
    {
        // Each node's store creates the table on its first call; CREATE TABLE IF NOT EXISTS alone is not safe against a
        // concurrent create (a unique violation in the catalog), and the failed call would fail the actors it loads.
        for (int round = 0; round < 5; round++)
        {
            var stores = _stores.CreateShared(8);
            await Task.WhenAll(stores.Select(static s => Load(s, A)));
            foreach (var store in stores.Distinct()) if (store is IAsyncDisposable d) await d.DisposeAsync();
        }
    }

    [Test]
    public async Task WritesAreVersionedAndFenceStaleOwners()
    {
        var store = _stores.Create();
        Assert.That((await Load(store, A))[0].Version, Is.Zero, "nothing stored yet");

        Assert.That(await Save(store, new StoredWrite(A, 0, new byte[] { 1 }), new StoredWrite(C, 0, new byte[] { 3, 3 })),
            Is.EqualTo(new[] { true, true }), "first writes insert");
        Assert.That(await Save(store, new StoredWrite(A, 0, new byte[] { 9 })), Is.EqualTo(new[] { false }),
            "a second first write means another owner wrote first");
        Assert.That(await Save(store, new StoredWrite(A, 1, new byte[] { 2 }), new StoredWrite(C, 5, new byte[] { 9 })),
            Is.EqualTo(new[] { true, false }), "a write replaces only the version it read");
        Assert.That(await Save(store, new StoredWrite(A, 1, new byte[] { 9 })), Is.EqualTo(new[] { false }), "version 1 is gone");

        var records = await Load(store, B, A, C);
        Assert.That(records[0].Version, Is.Zero, "a key without a record");
        Assert.That((records[1].Version, records[1].Data.ToArray()), Is.EqualTo((2L, new byte[] { 2 })));
        Assert.That((records[2].Version, records[2].Data.ToArray()), Is.EqualTo((1L, new byte[] { 3, 3 })), "any type id and key round-trip");
    }

    [Test]
    public async Task ABatchLoadsAndSavesManyRecords()
    {
        var store = _stores.Create();
        var writes = new StoredWrite[512];
        for (int i = 0; i < writes.Length; i++) writes[i] = new StoredWrite(new StoredKey(3, i), 0, BitConverter.GetBytes(i));
        Assert.That(await Save(store, writes), Is.All.True);

        var keys = new StoredKey[writes.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = new StoredKey(3, keys.Length - 1 - i);
        var records = await Load(store, keys);
        for (int i = 0; i < keys.Length; i++)
            Assert.That(BitConverter.ToInt32(records[i].Data.Span), Is.EqualTo(keys.Length - 1 - i), "records come back in the keys' order");
    }
}

/// <summary>What only a database store faces: connections lost to a restart or a failover.</summary>
public class PostgreSqlStoreTests
{
    private readonly Stores _stores = new(Stores.PostgreSql);
    private static readonly StoredKey A = new(7, 1);

    [OneTimeTearDown]
    public Task DropStores() => _stores.DisposeAsync().AsTask();

    [Test]
    public async Task AStoreRecoversFromLostConnections()
    {
        var store = _stores.Create();
        Assert.That(await ActorStoreTests.Save(store, new StoredWrite(A, 0, new byte[] { 1 })), Is.EqualTo(new[] { true }));
        await Stores.DropConnections();

        // The call that finds its connection gone may fail (the shard retries it); the next one reconnects.
        try { await ActorStoreTests.Save(store, new StoredWrite(A, 1, new byte[] { 2 })); }
        catch (Npgsql.NpgsqlException) { }
        var record = (await ActorStoreTests.Load(store, A))[0];
        if (record.Version == 1) Assert.That(await ActorStoreTests.Save(store, new StoredWrite(A, 1, new byte[] { 2 })), Is.EqualTo(new[] { true }));
        Assert.That((await ActorStoreTests.Load(store, A))[0].Data.ToArray(), Is.EqualTo(new byte[] { 2 }));
    }
}
