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
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IActorStore, string> Tables = new();

    public static string Connection => ConnectionString!;

    public static string TableOf(IActorStore store) => Tables.TryGetValue(store, out var table) ? table : throw new ArgumentException("not a store of this fixture");

    /// <summary>A key's record as stored, without claiming it (a claim would fence the node running the actor).</summary>
    public static async Task<StoredRecord> Peek(IActorStore store, StoredKey key)
    {
        if (store is InMemoryActorStore memory) return memory.Peek(key);
        Assert.That(Tables.TryGetValue(store, out var table), Is.True, "a store of this fixture");
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await using var read = dataSource.CreateCommand($"SELECT version, data FROM {table} WHERE type_id = $1 AND key = $2");
        read.Parameters.Add(new NpgsqlParameter<int> { TypedValue = unchecked((int)key.HandlerTypeId) });
        read.Parameters.Add(new NpgsqlParameter<long> { TypedValue = key.Key });
        try
        {
            await using var reader = await read.ExecuteReaderAsync();
            return await reader.ReadAsync() ? new StoredRecord(reader.GetInt64(0), reader.GetFieldValue<byte[]>(1)) : default;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return default; // the store creates its table on its first call
        }
    }

    private PostgreSqlActorStore Postgres(string table)
    {
        var store = new PostgreSqlActorStore(ConnectionString!, table);
        Tables.Add(store, table);
        return store;
    }

    /// <summary>A store ready to use, as a node starts it (<see cref="IActorStore.InitializeAsync"/>).</summary>
    public async Task<IActorStore> Ready()
    {
        var store = Create();
        await store.InitializeAsync();
        return store;
    }

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
        return Postgres(table);
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
        return [.. Enumerable.Range(0, count).Select(_ => (IActorStore)Postgres(table))];
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

/// <summary>What every <see cref="IActorStore"/> guarantees: batched claims, versioned writes that fence stale owners.</summary>
[TestFixtureSource(typeof(Stores), nameof(Stores.Kinds))]
public class ActorStoreTests(string kind)
{
    private readonly Stores _stores = new(kind);

    [OneTimeTearDown]
    public Task DropStores() => _stores.DisposeAsync().AsTask();

    private static readonly StoredKey A = new(7, 1), B = new(7, 2), C = new(unchecked((uint)-5), long.MaxValue);

    internal static async Task<StoredRecord[]> Claim(IActorStore store, params StoredKey[] keys)
    {
        var records = new StoredRecord[keys.Length];
        await store.ClaimAsync(keys, records);
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
        // Each node's store creates the table as the node starts; CREATE TABLE IF NOT EXISTS alone is not safe against a
        // concurrent create (a unique violation in the catalog), and the node whose create failed would not start.
        for (int round = 0; round < 5; round++)
        {
            var stores = _stores.CreateShared(8);
            await Task.WhenAll(stores.Select(static s => s.InitializeAsync().AsTask()));
            await Claim(stores[0], A);
            foreach (var store in stores.Distinct()) if (store is IAsyncDisposable d) await d.DisposeAsync();
        }
    }

    [Test]
    public async Task AStoreThatDoesNotCreateItsTableRefusesToStartWithoutIt()
    {
        // A migration job or a DBA owns the schema (createTable: false): a missing table stops the node at start, naming
        // the statement to run, instead of failing its first loads.
        if (kind != Stores.PostgreSql) Assert.Ignore("only a database has a schema");
        var created = (PostgreSqlActorStore)_stores.Create();
        var table = Stores.TableOf(created);
        await using var store = new PostgreSqlActorStore(Stores.Connection, table, createTable: false);
        var refused = Assert.ThrowsAsync<InvalidOperationException>(async () => await store.InitializeAsync());
        Assert.That(refused!.Message, Does.Contain(table).And.Contain("CREATE TABLE"));
        await created.InitializeAsync(); // what the migration job does
        await store.InitializeAsync();
    }

    [Test]
    public async Task WritesAreVersionedAndFenceStaleOwners()
    {
        var store = await _stores.Ready();
        Assert.That((await Stores.Peek(store, A)).Version, Is.Zero, "nothing stored yet");

        Assert.That(await Save(store, new StoredWrite(A, 0, new byte[] { 1 }), new StoredWrite(C, 0, new byte[] { 3, 3 })),
            Is.EqualTo(new[] { true, true }), "first writes insert");
        Assert.That(await Save(store, new StoredWrite(A, 0, new byte[] { 9 })), Is.EqualTo(new[] { false }),
            "a second first write means another owner wrote first");
        Assert.That(await Save(store, new StoredWrite(A, 1, new byte[] { 2 }), new StoredWrite(C, 5, new byte[] { 9 })),
            Is.EqualTo(new[] { true, false }), "a write replaces only the version it read");
        Assert.That(await Save(store, new StoredWrite(A, 1, new byte[] { 9 })), Is.EqualTo(new[] { false }), "version 1 is gone");

        var records = await Claim(store, B, A, C);
        Assert.That((records[0].Version, records[0].Data.Length), Is.EqualTo((1L, 0)), "a key without a record is claimed empty");
        Assert.That((records[1].Version, records[1].Data.ToArray()), Is.EqualTo((3L, new byte[] { 2 })), "one above the last write");
        Assert.That((records[2].Version, records[2].Data.ToArray()), Is.EqualTo((2L, new byte[] { 3, 3 })), "any type id and key round-trip");
    }

    [Test]
    public async Task AClaimFencesWhoeverLoadedTheRecordBefore()
    {
        // A split or a stale owner: whoever claimed last owns the record; the earlier one's writes are refused.
        var store = await _stores.Ready();
        Assert.That(await Save(store, new StoredWrite(A, 0, new byte[] { 1 })), Is.EqualTo(new[] { true }));
        var stale = (await Claim(store, A))[0];
        var owner = (await Claim(store, A))[0];
        Assert.That(owner.Version, Is.EqualTo(stale.Version + 1));
        Assert.That(await Save(store, new StoredWrite(A, stale.Version, new byte[] { 8 })), Is.EqualTo(new[] { false }), "the earlier claim is fenced");
        Assert.That(await Save(store, new StoredWrite(A, owner.Version, new byte[] { 9 })), Is.EqualTo(new[] { true }));
        Assert.That((await Stores.Peek(store, A)).Data.ToArray(), Is.EqualTo(new byte[] { 9 }));

        var fresh = new StoredKey(7, 42); // never written: claimed too, so a first write expecting none is refused
        var claimed = (await Claim(store, fresh))[0];
        Assert.That(await Save(store, new StoredWrite(fresh, 0, new byte[] { 1 })), Is.EqualTo(new[] { false }));
        Assert.That(await Save(store, new StoredWrite(fresh, claimed.Version, new byte[] { 1 })), Is.EqualTo(new[] { true }));
    }

    [Test]
    public async Task ABatchLoadsAndSavesManyRecords()
    {
        var store = await _stores.Ready();
        var writes = new StoredWrite[512];
        for (int i = 0; i < writes.Length; i++) writes[i] = new StoredWrite(new StoredKey(3, i), 0, BitConverter.GetBytes(i));
        Assert.That(await Save(store, writes), Is.All.True);

        var keys = new StoredKey[writes.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = new StoredKey(3, keys.Length - 1 - i);
        var records = await Claim(store, keys);
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
        var store = await _stores.Ready();
        Assert.That(await ActorStoreTests.Save(store, new StoredWrite(A, 0, new byte[] { 1 })), Is.EqualTo(new[] { true }));
        await Stores.DropConnections();

        // The call that finds its connection gone may fail (the shard retries it); the next one reconnects.
        try { await ActorStoreTests.Save(store, new StoredWrite(A, 1, new byte[] { 2 })); }
        catch (Npgsql.NpgsqlException) { }
        var record = await Stores.Peek(store, A);
        if (record.Version == 1) Assert.That(await ActorStoreTests.Save(store, new StoredWrite(A, 1, new byte[] { 2 })), Is.EqualTo(new[] { true }));
        Assert.That((await Stores.Peek(store, A)).Data.ToArray(), Is.EqualTo(new byte[] { 2 }));
    }
}
