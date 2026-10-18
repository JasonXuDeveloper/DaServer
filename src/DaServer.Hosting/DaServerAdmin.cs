using System.Security.Cryptography;
using System.Text;
using DaServer.Cluster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DaServer.Hosting;

/// <summary>
/// The node's admin endpoints (A5) under <c>/admin</c>, for operators, the Aspire dashboard's commands and tests: the
/// membership as this node sees it, its statistics, a drain (the node leaves and stops gracefully: what SIGTERM does),
/// and unloading an actor type's actors. Each request carries <c>Authorization: Bearer {token}</c>, the token derived from
/// the cluster's signing secret (<see cref="AdminToken"/>); a node without a secret maps none of them.
/// </summary>
internal static class DaServerAdmin
{
    public static void Map(IEndpointRouteBuilder endpoints, Node node)
    {
        if (node.Options.SigningSecret is not { Length: > 0 } secret) return;
        var token = Encoding.UTF8.GetBytes("Bearer " + AdminToken.For(secret));
        var admin = endpoints.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
            Authorized(context.HttpContext, token) ? await next(context) : Results.Unauthorized());
        admin.MapGet("/membership", static (Node node) => Results.Json(Membership(node)));
        admin.MapGet("/stats", static (Node node) => Results.Json(Stats(node)));
        admin.MapPost("/drain", static (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication(); // leave, drain clients, unload and save every actor, then exit
            return Results.Accepted();
        });
        admin.MapPost("/unload/{type}", static (string type, Node node) =>
            node.UnloadActors(type) ? Results.Accepted() : Results.NotFound($"This node hosts no actor type '{type}'"));
    }

    private static bool Authorized(HttpContext http, byte[] token) =>
        http.Request.Headers.Authorization.ToString() is { Length: > 0 } given &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), token);

    private static object Membership(Node node)
    {
        var view = node.View;
        var members = new List<object>();
        foreach (var m in view.Members)
            members.Add(new
            {
                m.Name, Id = m.NodeId.ToString("x16"), Address = $"{m.Host}:{m.Port}", Status = m.Status.ToString(), m.Incarnation,
                m.Load, UpSince = DateTimeOffset.FromUnixTimeMilliseconds(m.UpSince), m.Build,
            });
        return new { Self = node.Options.Name, Cluster = node.Options.ClusterName, Coordinator = view.Coordinator?.Name, Members = members };
    }

    private static object Stats(Node node) => new
    {
        node.Options.Name, Ready = node.IsReady, Actors = node.ActorCount, Shards = node.ShardCount, Queued = node.TotalQueueLength(),
        node.Failed, node.TickOverruns, node.StalledShards,
        Dropped = new
        {
            QueueFull = node.Dropped(DropReason.QueueFull), MailboxFull = node.Dropped(DropReason.MailboxFull),
            Expired = node.Dropped(DropReason.Expired),
        },
    };
}
