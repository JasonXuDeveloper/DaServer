using DaServer.Ingress;
using DaServer.Protocol;
using DaServer.Rpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DaServer.Hosting;

/// <summary>
/// The node's HTTP plane (ADR-046): ASP.NET Core minimal APIs whose requests enter the cluster through the node's
/// ingress, each with an origin of its own and its caller's budget. Endpoints reach actors only through messages.
/// </summary>
public static class DaServerHttpExtensions
{
    /// <summary>Request header: the caller's remaining time in milliseconds (capped at the node's call timeout).</summary>
    public const string BudgetHeader = "DaServer-Budget";

    /// <summary>
    /// Run a node in this web application (<see cref="DaServerHostingExtensions.AddDaServer"/>) with Kestrel on
    /// <c>{section}:HttpPort</c> at the node's listen address; without a port Kestrel keeps the host's own URLs
    /// (<c>ASPNETCORE_URLS</c>), or a free port when there are none.
    /// </summary>
    public static WebApplicationBuilder AddDaServer(this WebApplicationBuilder builder, string section = "DaServer")
    {
        if (builder.Configuration[$"{section}:Probe"] is { Length: > 0 } probe) ProbeAndExit(builder.Configuration, section, probe);
        ((IHostApplicationBuilder)builder).AddDaServer(section);
        builder.WebHost.ConfigureKestrel(static kestrel => kestrel.AddServerHeader = false);
        var listen = builder.Configuration[$"{section}:ListenAddress"] ?? "0.0.0.0";
        if (builder.Configuration[$"{section}:HttpPort"] is { Length: > 0 } port)
            builder.WebHost.UseUrls($"http://{listen}:{port}");
        else if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
            builder.WebHost.UseUrls($"http://{listen}:0");
        return builder;
    }

    /// <summary>
    /// Probe mode (<c>{section}:Probe</c> = a path such as <c>/health</c>; a container's health check runs the node's
    /// executable with <c>--DaServer:Probe=/health</c>, A3): ask the running node at <c>{section}:HttpPort</c> on this
    /// machine and exit 0 when it answers 200, 1 otherwise, before anything starts.
    /// </summary>
    private static void ProbeAndExit(IConfiguration configuration, string section, string path)
    {
        int code = 1;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = http.GetAsync($"http://127.0.0.1:{configuration[$"{section}:HttpPort"]}{path}").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode) code = 0;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Console.Error.WriteLine(e.Message);
        }

        Environment.Exit(code);
    }

    /// <summary>
    /// Map the node's own endpoints (Program.cs calls it after <c>Build</c>):
    /// <c>/health</c> (ready: joined, not leaving), <c>/alive</c> (no stalled shard), <c>/metrics</c> (Prometheus) and the
    /// admin endpoints under <c>/admin</c> (membership, stats, drain, unload; <see cref="DaServerAdmin"/>).
    /// On client-facing nodes: <c>/clients/ws</c>, client connections over WebSocket (browsers, WebGL builds).
    /// </summary>
    public static WebApplication MapDaServer(this WebApplication app)
    {
        app.MapHealthChecks("/health"); // readiness: every check
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = static r => r.Tags.Contains(DaServerHostingExtensions.LiveTag) });
        app.MapPrometheusScrapingEndpoint("/metrics");
        DaServerAdmin.Map(app);
        if (app.Services.GetRequiredService<Node>().Feature<DaServer.Clients.ClientConnections>() is not { } clients) return app;
        app.UseWebSockets();
        app.Map("/clients/ws", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var socket = await http.WebSockets.AcceptWebSocketAsync();
            var channel = new DaServer.Net.WebSocketChannel(socket, clients.Channels, clients.MaxFrameSize, clients.MaxPendingSendBytes,
                releaseIdle: true); // a crowd of idle browsers holds no receive buffers
            clients.Open(channel);
            await channel.RunAsync(); // the request lives as long as the client's connection
        });
        return app;
    }

    /// <summary>POST <paramref name="pattern"/>: the JSON body asked of the service handling it; the reply as JSON.</summary>
    public static RouteHandlerBuilder MapAsk<TRequest, TReply>(this IEndpointRouteBuilder endpoints, string pattern)
        where TRequest : struct, IRequest<TReply> =>
        endpoints.MapPost(pattern, static async (HttpContext http, TRequest request, Node node) =>
        {
            IIngress ingress = ((Features.INodeContext)node).Ingress;
            return await Reply(http, ingress.Ask<TRequest, TReply>(in request, OriginOf(node), BudgetOf(http)));
        });

    /// <summary>POST <paramref name="pattern"/> with an <c>{id}</c>: the JSON body asked of the actor <typeparamref name="TId"/>(id).</summary>
    public static RouteHandlerBuilder MapAsk<TId, TRequest, TReply>(this IEndpointRouteBuilder endpoints, string pattern)
        where TId : struct, IActorId where TRequest : struct, IRequest<TReply> =>
        endpoints.MapPost(pattern, static async (HttpContext http, long id, TRequest request, Node node) =>
        {
            IIngress ingress = ((Features.INodeContext)node).Ingress;
            return await Reply(http, ingress.Ask<TId, TRequest, TReply>(ActorIds<TId>.Of(id), in request, OriginOf(node), BudgetOf(http)));
        });

    /// <summary>POST <paramref name="pattern"/>: the JSON body sent one-way to the service handling it (202 Accepted).</summary>
    public static RouteHandlerBuilder MapSend<T>(this IEndpointRouteBuilder endpoints, string pattern) where T : struct =>
        endpoints.MapPost(pattern, static (T message, Node node) =>
        {
            IIngress ingress = ((Features.INodeContext)node).Ingress;
            ingress.Send(TypeId<T>.Value, 0, 0, Nino.Core.NinoSerializer.Serialize(message), OriginOf(node));
            return Results.Accepted();
        });

    private static Origin OriginOf(Node node) => new(node.Id, ((Features.INodeContext)node).NewOriginId());

    private static TimeSpan BudgetOf(HttpContext http) =>
        long.TryParse(http.Request.Headers[BudgetHeader], out long ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    private static async ValueTask<IResult> Reply<TReply>(HttpContext http, ValueTask<TReply> call)
    {
        try
        {
            return Results.Json(await call);
        }
        catch (RpcException e)
        {
            if (e.RetryAfter > TimeSpan.Zero || e.Status is RpcStatus.Overloaded or RpcStatus.RateLimited)
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(e.RetryAfter.TotalSeconds)).ToString();
            return Results.Problem(e.Message, statusCode: StatusCodeOf(e.Status));
        }
    }

    /// <summary>The HTTP status of a failed call.</summary>
    internal static int StatusCodeOf(RpcStatus status) => status switch
    {
        RpcStatus.RateLimited => StatusCodes.Status429TooManyRequests,
        RpcStatus.Overloaded or RpcStatus.NotHosted or RpcStatus.Disconnected => StatusCodes.Status503ServiceUnavailable,
        RpcStatus.Timeout => StatusCodes.Status504GatewayTimeout,
        RpcStatus.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError,
    };
}
