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

    public IActorStore Create()
    {
        if (kind == Memory) return new InMemoryActorStore();
        if (string.IsNullOrEmpty(ConnectionString)) Assert.Ignore("DASERVER_TEST_POSTGRES is not set");
        string table = "actors_" + Guid.NewGuid().ToString("N");
        _tables.Add(table);
        return new PostgreSqlActorStore(ConnectionString!, table);
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

    private static async Task<StoredRecord[]> Load(IActorStore store, params StoredKey[] keys)
    {
        var records = new StoredRecord[keys.Length];
        await store.LoadAsync(keys, records);
        return records;
    }

    private static async Task<bool[]> Save(IActorStore store, params StoredWrite[] writes)
    {
        var accepted = new bool[writes.Length];
        await store.SaveAsync(writes, accepted);
        return accepted;
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
