using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace DaServer.Persistence;

/// <summary>
/// Actor records in PostgreSQL (ADR-068): a row per actor, <c>(type_id, key)</c> its primary key, the record's bytes
/// in <c>data</c> and the version that fences writes. A shard's batch is one round trip either way: loads are one query
/// over the batch's keys, saves one batch of statements, each written only if the row still holds the expected version.
/// The table is created on first use.
/// </summary>
public sealed partial class PostgreSqlActorStore : IActorStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly string _load, _insert, _update, _create;
    private Task? _created;

    /// <param name="dataSource">The database (one per cluster, or one table per cluster in a shared database).</param>
    /// <param name="table">The records' table, optionally schema-qualified (<c>game.actors</c>).</param>
    public PostgreSqlActorStore(NpgsqlDataSource dataSource, string table = "daserver_actors")
    {
        if (!TableName().IsMatch(table)) throw new ArgumentException($"Not a table name: '{table}'", nameof(table));
        _dataSource = dataSource;
        _load = $"SELECT k.i, t.version, t.data FROM unnest($1, $2) WITH ORDINALITY AS k(type_id, key, i) " +
                $"JOIN {table} t ON t.type_id = k.type_id AND t.key = k.key";
        _insert = $"INSERT INTO {table} (type_id, key, version, data) VALUES ($1, $2, 1, $3) ON CONFLICT DO NOTHING";
        _update = $"UPDATE {table} SET version = version + 1, data = $3, updated_at = now() " +
                  $"WHERE type_id = $1 AND key = $2 AND version = $4";
        _create = $"CREATE TABLE IF NOT EXISTS {table} (type_id integer NOT NULL, key bigint NOT NULL, " +
                        "version bigint NOT NULL, data bytea NOT NULL, updated_at timestamptz NOT NULL DEFAULT now(), " +
                        "PRIMARY KEY (type_id, key))";
    }

    /// <param name="connectionString">The database's connection string.</param>
    /// <param name="table">The records' table, optionally schema-qualified.</param>
    public PostgreSqlActorStore(string connectionString, string table = "daserver_actors")
        : this(NpgsqlDataSource.Create(connectionString), table) =>
        _ownsDataSource = true;

    public ValueTask DisposeAsync() => _ownsDataSource ? _dataSource.DisposeAsync() : default;

    public async ValueTask LoadAsync(ReadOnlyMemory<StoredKey> keys, Memory<StoredRecord> records, CancellationToken cancellationToken = default)
    {
        await Created();
        var types = new int[keys.Length];
        var ids = new long[keys.Length];
        Split(keys.Span, types, ids);

        await using var command = _dataSource.CreateCommand(_load);
        command.Parameters.Add(new NpgsqlParameter<int[]> { TypedValue = types });
        command.Parameters.Add(new NpgsqlParameter<long[]> { TypedValue = ids });
        records.Span.Clear(); // a key without a row has no record
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Span[(int)reader.GetInt64(0) - 1] = new StoredRecord(reader.GetInt64(1), reader.GetFieldValue<byte[]>(2));
    }

    public async ValueTask SaveAsync(ReadOnlyMemory<StoredWrite> writes, Memory<bool> accepted, CancellationToken cancellationToken = default)
    {
        await Created();
        await using var batch = _dataSource.CreateBatch();
        for (int i = 0; i < writes.Length; i++)
        {
            var write = writes.Span[i];
            // A first write inserts (refused if a row exists: another owner wrote first); later ones replace the version
            // they read.
            var command = new NpgsqlBatchCommand(write.ExpectedVersion == 0 ? _insert : _update);
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = unchecked((int)write.Key.HandlerTypeId) });
            command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = write.Key.Key });
            command.Parameters.Add(new NpgsqlParameter<ReadOnlyMemory<byte>> { TypedValue = write.Data, NpgsqlDbType = NpgsqlDbType.Bytea });
            if (write.ExpectedVersion != 0) command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = write.ExpectedVersion });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
        for (int i = 0; i < writes.Length; i++) accepted.Span[i] = batch.BatchCommands[i].RecordsAffected == 1;
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

    private static void Split(ReadOnlySpan<StoredKey> keys, int[] types, long[] ids)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            types[i] = unchecked((int)keys[i].HandlerTypeId);
            ids[i] = keys[i].Key;
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
