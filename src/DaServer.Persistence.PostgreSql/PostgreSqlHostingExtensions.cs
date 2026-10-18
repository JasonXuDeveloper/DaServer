using DaServer.Persistence;
using DaServer.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace DaServer.Hosting;

public static class PostgreSqlHostingExtensions
{
    /// <summary>
    /// Keep this node's actor records (<c>[Persist]</c> components, ADR-068) in PostgreSQL: the connection string
    /// <c>ConnectionStrings:{connectionName}</c> (Aspire: <c>WithActorStore</c> on the node group). The database comes
    /// from Aspire's Npgsql integration (a data source keyed by <paramref name="connectionName"/>, its health check,
    /// tracing and metrics); its health joins readiness (<c>/health</c>): a node that cannot load or save its actors
    /// takes no traffic.
    /// </summary>
    /// <param name="table">The records' table, optionally schema-qualified.</param>
    /// <param name="createTable">
    /// Create the table when the node starts (the default); false where a migration job or a DBA owns the schema
    /// (<see cref="PostgreSqlActorStore.Schema"/>): the node then checks the table and does not start without it.
    /// </param>
    public static IHostApplicationBuilder AddPostgreSqlActorStore(this IHostApplicationBuilder builder,
        string connectionName = "actors", string table = "daserver_actors", bool createTable = true)
    {
        builder.AddKeyedNpgsqlDataSource(connectionName); // keyed: the application's own database stays its default one
        builder.Services.AddSingleton(sp => new PostgreSqlActorStore(sp.GetRequiredKeyedService<NpgsqlDataSource>(connectionName), table, createTable));
        builder.Services.AddOptions<NodeOptions>().Configure<PostgreSqlActorStore>(static (node, store) => node.ActorStore = store);
        return builder;
    }
}
