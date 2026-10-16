using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace DaServer.Persistence;

/// <summary>
/// Actor records in PostgreSQL (ADR-068): a row per actor, <c>(type_id, key)</c> its primary key, the record's bytes
/// in <c>data</c> and the version that fences writes. A shard's batch is one statement either way: loads one query over
/// the batch's keys, saves one statement over the batch's arrays that writes a record only if its row still holds the
/// expected version (the record bytes are passed as they are, not copied).
/// The table is created on first use.
/// </summary>
public sealed partial class PostgreSqlActorStore : IActorStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly string _load, _save, _create;
    private Task? _created;

    /// <param name="dataSource">The database (one per cluster, or one table per cluster in a shared database).</param>
    /// <param name="table">The records' table, optionally schema-qualified (<c>game.actors</c>).</param>
    public PostgreSqlActorStore(NpgsqlDataSource dataSource, string table = "daserver_actors")
    {
        if (!TableName().IsMatch(table)) throw new ArgumentException($"Not a table name: '{table}'", nameof(table));
        _dataSource = dataSource;
        _load = $"SELECT k.i, t.version, t.data FROM unnest($1, $2) WITH ORDINALITY AS k(type_id, key, i) " +
                $"JOIN {table} t ON t.type_id = k.type_id AND t.key = k.key";
        // One statement per batch: a first write inserts (refused if a row exists: another owner wrote first), a later one
        // replaces the version it read; the ordinals of the accepted writes come back.
        _save = $"WITH w AS (SELECT * FROM unnest($1::integer[], $2::bigint[], $3::bigint[], $4::bytea[]) " +
                "WITH ORDINALITY AS w(type_id, key, expected, data, i)), " +
                $"ins AS (INSERT INTO {table} (type_id, key, version, data) SELECT type_id, key, 1, data FROM w " +
                "WHERE expected = 0 ON CONFLICT DO NOTHING RETURNING type_id, key), " +
                $"upd AS (UPDATE {table} t SET version = t.version + 1, data = w.data, updated_at = now() FROM w " +
                "WHERE w.expected <> 0 AND t.type_id = w.type_id AND t.key = w.key AND t.version = w.expected " +
                "RETURNING t.type_id, t.key) " +
                "SELECT w.i FROM w JOIN (SELECT * FROM ins UNION ALL SELECT * FROM upd) a ON a.type_id = w.type_id AND a.key = w.key";
        _create = $"CREATE TABLE IF NOT EXISTS {table} (type_id integer NOT NULL, key bigint NOT NULL, " +
                        "version bigint NOT NULL, data bytea NOT NULL, updated_at timestamptz NOT NULL DEFAULT now(), " +
                        "PRIMARY KEY (type_id, key))";
    }

    /// <param name="connectionString">The database's connection string.</param>
    /// <param name="table">The records' table, optionally schema-qualified.</param>
    public PostgreSqlActorStore(string connectionString, string table = "daserver_actors")
        : this(NpgsqlDataSource.Create(connectionString), table) =>
        _ownsDataSource = true;

    public async ValueTask DisposeAsync()
    {
        while (_columns.TryDequeue(out var columns)) await columns.Disconnect();
        if (_ownsDataSource) await _dataSource.DisposeAsync();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))] // no state machine allocated per call
    public async ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default)
    {
        await Created();
        var columns = Rent();
        try
        {
            foreach (var key in keys.Span)
            {
                columns.Types.Add(unchecked((int)key.HandlerTypeId));
                columns.Keys.Add(key.Key);
            }

            await Connected(columns, cancellationToken);
            records.Span.Clear(); // a key without a row has no record
            await using var reader = await columns.Load.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                records.Span[(int)reader.GetInt64(0) - 1] = new StoredRecord(reader.GetInt64(1), reader.GetFieldValue<byte[]>(2));
        }
        catch
        {
            await columns.Disconnect(); // the next call opens a fresh connection
            throw;
        }
        finally
        {
            Return(columns);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken cancellationToken = default)
    {
        await Created();
        var columns = Rent();
        try
        {
            foreach (var write in writes.Span)
            {
                columns.Types.Add(unchecked((int)write.Key.HandlerTypeId));
                columns.Keys.Add(write.Key.Key);
                columns.Expected.Add(write.ExpectedVersion);
                columns.Data.Add(write.Data); // the shard's pooled buffer, valid until this call completes: not copied
            }

            await Connected(columns, cancellationToken);
            accepted.Span.Clear();
            await using var reader = await columns.Save.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) accepted.Span[(int)reader.GetInt64(0) - 1] = true;
        }
        catch
        {
            await columns.Disconnect(); // the next call opens a fresh connection
            throw;
        }
        finally
        {
            Return(columns);
        }
    }

    /// <summary>
    /// The columns' own connection, opened once with both statements prepared (parsed and planned once by the server);
    /// reopened after a failure.
    /// </summary>
    private async ValueTask Connected(Columns columns, CancellationToken cancellationToken)
    {
        if (columns.Connection is { State: System.Data.ConnectionState.Open }) return;
        await columns.Disconnect();
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        columns.Connection = columns.Load.Connection = columns.Save.Connection = connection;
        await columns.Load.PrepareAsync(cancellationToken);
        await columns.Save.PrepareAsync(cancellationToken);
    }

    /// <summary>
    /// A call's parameters as columns (what <c>unnest</c> takes), its prepared commands and their connection, reused
    /// across calls: lists keep their capacity and are exactly as long as the batch, which rented arrays are not. One
    /// set per call in flight (at most one load and one save per shard), so the connections are bounded the same way.
    /// </summary>
    private sealed class Columns
    {
        public readonly List<int> Types = new();
        public readonly List<long> Keys = new();
        public readonly List<long> Expected = new();
        public readonly List<ReadOnlyMemory<byte>> Data = new();
        public readonly NpgsqlCommand Load, Save;
        public NpgsqlConnection? Connection;

        public Columns(PostgreSqlActorStore store)
        {
            Load = new NpgsqlCommand(store._load);
            Load.Parameters.Add(new NpgsqlParameter<List<int>> { TypedValue = Types });
            Load.Parameters.Add(new NpgsqlParameter<List<long>> { TypedValue = Keys });
            Save = new NpgsqlCommand(store._save);
            Save.Parameters.Add(new NpgsqlParameter<List<int>> { TypedValue = Types });
            Save.Parameters.Add(new NpgsqlParameter<List<long>> { TypedValue = Keys });
            Save.Parameters.Add(new NpgsqlParameter<List<long>> { TypedValue = Expected });
            Save.Parameters.Add(new NpgsqlParameter<List<ReadOnlyMemory<byte>>> { TypedValue = Data, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        }

        public void Clear()
        {
            Types.Clear();
            Keys.Clear();
            Expected.Clear();
            Data.Clear(); // drops the references to the shard's buffers
        }

        public async ValueTask Disconnect()
        {
            if (Connection == null) return;
            Load.Connection = Save.Connection = null;
            await Connection.DisposeAsync();
            Connection = null;
        }
    }

    private readonly ConcurrentQueue<Columns> _columns = new();

    private Columns Rent() => _columns.TryDequeue(out var columns) ? columns : new Columns(this);

    private void Return(Columns columns)
    {
        columns.Clear();
        _columns.Enqueue(columns);
    }

    /// <summary>Whether the database answers (readiness).</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = _dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// The table exists: created by the first call, again by a call after a failed attempt (the shard retries the batch).
    /// Concurrent first calls may both create it, which <c>IF NOT EXISTS</c> makes harmless.
    /// </summary>
    private Task Created()
    {
        var task = _created;
        if (task is null || task.IsFaulted || task.IsCanceled) _created = task = Create();
        return task;
    }

    private async Task Create()
    {
        await using var command = _dataSource.CreateCommand(_create);
        await command.ExecuteNonQueryAsync();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$")]
    private static partial Regex TableName();
}
