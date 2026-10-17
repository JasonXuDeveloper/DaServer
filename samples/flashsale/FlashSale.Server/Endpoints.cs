using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using DaServer;
using DaServer.Hosting;
using DaServer.Ingress;
using DaServer.Rpc;
using DaServer.Runtime;
using FlashSale.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace FlashSale.Server;

/// <summary>
/// The sale's HTTP API. Buyers queue (a signed ticket holds their position), wait until the room admits them (a signed
/// pass, checked here at the edge, no actor involved), then buy; any node serves any request. Once the room says the
/// sale is sold out, this node answers so itself: the late crowd never reaches the cluster.
/// <code>
/// POST /sales/{sale}/open?units=&amp;perSecond=    stock split over the shards, the room opened
/// POST /sales/{sale}/queue?user=                 { status: Waiting, ticket } or { status: SoldOut }
/// GET  /sales/{sale}/queue?user=&amp;ticket=        { status: Waiting } or { status: Admitted, pass } or { status: SoldOut }
/// POST /sales/{sale}/buy?user=&amp;pass=            { status }: Confirmed or SoldOut, the same on every retry
/// GET  /sales/{sale}/orders/{user}               { status }
/// GET  /sales/{sale}/stock                       { left }
/// </code>
/// </summary>
public static class Endpoints
{
    public static WebApplication MapFlashSale(this WebApplication app)
    {
        var passes = new Passes(app.Configuration["DaServer:SigningSecret"]);
        var soldOut = new ConcurrentDictionary<long, bool>(); // sales this node heard are sold out: answered here
        var sales = app.MapGroup("/sales/{sale:long}");

        sales.MapPost("/open", static async (long sale, int units, int perSecond, ICluster cluster) =>
        {
            await cluster.Ask(new WaitingRoomId(sale), new OpenRoom(perSecond)); // first: the shards report to an open room
            for (int shard = 0; shard < Sale.Shards; shard++)
                await cluster.Ask(StockShardId.Of(sale, shard), new Restock(units / Sale.Shards + (shard < units % Sale.Shards ? 1 : 0)));
            return Results.Ok(new { units });
        });

        sales.MapPost("/queue", async (long sale, long user, ICluster cluster) =>
        {
            if (soldOut.ContainsKey(sale)) return SoldOut;
            return await Answer(cluster.Ask(new WaitingRoomId(sale), new Join()), position =>
            {
                if (position != 0) return Results.Ok(new { status = "Waiting", ticket = passes.Issue(Passes.Ticket, sale, user, position) });
                soldOut[sale] = true;
                return SoldOut;
            });
        });

        sales.MapGet("/queue", async (long sale, long user, string ticket, ICluster cluster) =>
        {
            if (soldOut.ContainsKey(sale)) return SoldOut;
            if (!passes.TryRead(ticket, Passes.Ticket, sale, user, out long position)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await Answer(cluster.Ask(new WaitingRoomId(sale), new Admitted(position)), admission =>
            {
                if (admission == Admission.Admitted) return Results.Ok(new { status = "Admitted", pass = passes.Issue(Passes.Pass, sale, user, position) });
                if (admission == Admission.Waiting) return Results.Ok(new { status = "Waiting" });
                soldOut[sale] = true;
                return SoldOut;
            });
        });

        sales.MapPost("/buy", async (long sale, long user, string pass, ICluster cluster) =>
        {
            if (!passes.TryRead(pass, Passes.Pass, sale, user, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await Answer(cluster.Ask(OrderId.Of(sale, user), new Buy()), static status => Results.Ok(new { status = status.ToString() }));
        });

        sales.MapGet("/orders/{user:long}", static async (long sale, long user, ICluster cluster) =>
            await Answer(cluster.Ask(OrderId.Of(sale, user), new GetOrder()), static status => Results.Ok(new { status = status.ToString() })));

        sales.MapGet("/stock", static async (long sale, ICluster cluster) =>
        {
            int left = 0;
            for (int shard = 0; shard < Sale.Shards; shard++) left += await cluster.Ask(StockShardId.Of(sale, shard), new UnitsLeft());
            return Results.Ok(new { left });
        });

        return app;
    }

    private static readonly IResult SoldOut = Results.Ok(new { status = "SoldOut" });

    /// <summary>The reply as a result, or the refusal as its HTTP status (503 overloaded, 504 timed out): buyers retry.</summary>
    private static async Task<IResult> Answer<T>(Reply<T> call, Func<T, IResult> ok)
    {
        try
        {
            return ok(await call);
        }
        catch (RpcException e)
        {
            return Results.StatusCode(DaServerHttpExtensions.StatusCodeOf(e.Status));
        }
    }
}

/// <summary>
/// Signed tickets (a queue position) and passes (admitted to buy), bound to a sale and a user: the edge checks them
/// without asking any actor (<see cref="SignedToken"/>, the cluster's signing secret, so every node accepts them).
/// </summary>
public sealed class Passes(string? secret)
{
    public const byte Ticket = 1, Pass = 2;
    private const int ContentSize = 1 + 3 * sizeof(long);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(secret ?? "flash-sale-development-secret-only");

    public string Issue(byte kind, long sale, long user, long position)
    {
        Span<byte> token = stackalloc byte[SignedToken.SizeOf(ContentSize)];
        var content = SignedToken.Content(token);
        content[0] = kind;
        BinaryPrimitives.WriteInt64LittleEndian(content[1..], sale);
        BinaryPrimitives.WriteInt64LittleEndian(content[9..], user);
        BinaryPrimitives.WriteInt64LittleEndian(content[17..], position);
        SignedToken.Sign(token, _secret, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)Lifetime.TotalMilliseconds);
        return Base64Url.EncodeToString(token);
    }

    public bool TryRead(string text, byte kind, long sale, long user, out long position)
    {
        position = 0;
        Span<byte> token = stackalloc byte[SignedToken.SizeOf(ContentSize)];
        if (text.Length != Base64Url.GetEncodedLength(token.Length) || !Base64Url.IsValid(text) ||
            !Base64Url.TryDecodeFromChars(text, token, out int length) || length != token.Length) return false;
        if (!SignedToken.TryRead(token, _secret, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var content)) return false;
        if (content[0] != kind || BinaryPrimitives.ReadInt64LittleEndian(content[1..]) != sale ||
            BinaryPrimitives.ReadInt64LittleEndian(content[9..]) != user) return false;
        position = BinaryPrimitives.ReadInt64LittleEndian(content[17..]);
        return true;
    }
}
