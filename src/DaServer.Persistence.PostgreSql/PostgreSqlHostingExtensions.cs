using DaServer.Persistence;
using DaServer.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DaServer.Hosting;

public static class PostgreSqlHostingExtensions
{
    /// <summary>
    /// Keep this node's actor records (<c>[Persist]</c> components, ADR-068) in PostgreSQL: the connection string
    /// <c>ConnectionStrings:{connectionName}</c> (Aspire: <c>WithActorStore</c> on the node group). The database's
    /// health joins readiness (<c>/health</c>): a node that cannot load or save its actors takes no traffic.
    /// </summary>
    /// <param name="table">The records' table, created on first use; optionally schema-qualified.</param>
    public static IHostApplicationBuilder AddPostgreSqlActorStore(this IHostApplicationBuilder builder,
        string connectionName = "actors", string table = "daserver_actors")
    {
        string connectionString = builder.Configuration.GetConnectionString(connectionName) ??
            throw new InvalidOperationException($"No connection string '{connectionName}': set ConnectionStrings:{connectionName} " +
                                                "(the AppHost does with WithActorStore)");
        builder.Services.AddSingleton(_ => new PostgreSqlActorStore(connectionString, table));
        builder.Services.AddOptions<NodeOptions>().Configure<PostgreSqlActorStore>(static (node, store) => node.ActorStore = store);
        builder.Services.AddHealthChecks().AddCheck<StoreReadiness>("daserver-store");
        return builder;
    }
}

/// <summary>Ready only while the store answers: actors could neither load nor be saved otherwise.</summary>
internal sealed class StoreReadiness(PostgreSqlActorStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await store.PingAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("the actor store does not answer");
}
