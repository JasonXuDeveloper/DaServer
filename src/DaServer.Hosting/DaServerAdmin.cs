using System.Security.Cryptography;
using System.Text;
using DaServer.Cluster;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DaServer.Hosting;

/// <summary>
/// The node's admin endpoints (A5) under <c>/admin</c>, for operators, the Aspire dashboard's commands and tests: the
/// membership as this node sees it, its statistics, a drain (the node leaves and stops gracefully: what SIGTERM does),
/// and unloading an actor type's actors. They require the authorization policy <see cref="Policy"/>: by default a bearer
/// token derived from the cluster's signing secret (<see cref="AdminToken"/>, the accepted secret's too while it
/// rotates), so without a secret every request is refused. Redefine the policy after <c>AddDaServer</c>
/// (<c>AddAuthorization(o =&gt; o.AddPolicy(DaServerAdmin.Policy, ...))</c>) to use JWT, mTLS or any scheme of the host.
/// </summary>
public static class DaServerAdmin
{
    /// <summary>The authorization policy the admin endpoints require.</summary>
    public const string Policy = "DaServer.Admin";

    /// <summary>The default policy's authentication scheme: the token the signing secret derives.</summary>
    public const string Scheme = "DaServer.AdminToken";

    internal static void AddServices(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AdminTokenHandler>(Scheme, null);
        services.AddAuthorization(static o =>
            o.AddPolicy(Policy, static p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
    }

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/admin").RequireAuthorization(Policy);
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

/// <summary>
/// The default admin scheme: <c>Authorization: Bearer {token}</c> with the token of the signing secret or of the secret a
/// rotation still accepts, compared in constant time. No secret: nobody is authenticated.
/// </summary>
internal sealed class AdminTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IOptions<NodeOptions> node) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private readonly SigningKeys _tokens = SigningKeys.Of(node.Value, AdminToken.For);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The scheme is case-insensitive (RFC 9110); the token is compared in constant time.
        if (_tokens.Current == null || Request.Headers.Authorization.ToString() is not { Length: > 7 } header ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var given = Encoding.UTF8.GetBytes(header.AsSpan(7).Trim().ToString());
        if (!Array.Exists(_tokens.Verifying, token => CryptographicOperations.FixedTimeEquals(given, token)))
            return Task.FromResult(AuthenticateResult.Fail("Not the cluster's admin token"));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "daserver-admin")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
