using DaServer;
using DaServer.Protocol;
using DaServer.Runtime;
using EasyEcs;
using FlashSale.Components;
using FlashSale.Contracts;

namespace FlashSale.Server;

/// <summary>
/// The sale as clients see it: their messages turned into asks of the sale's actors. Stateless: what a shard's gate
/// remembers is the sales it heard about (<see cref="KnownSale"/> entities of its world), so every node's gate serves
/// every buyer and the gates scale with the nodes. A request served in steps is an entity while it waits (see
/// <see cref="Begin"/>): each answer finds what the next step needs there.
/// </summary>
[Service]
public static class Gate
{
    // ---------------------------------------------------------------- opening a sale

    [Handle]
    internal static Answer<bool> On(in OpenSale m, in Context ctx)
    {
        Begin(in ctx, out var to).Set(new Opening(m));
        ctx.Ask(new SaleId(m.Sale), new Open(m.PerSecond), to); // first: the shards report to an open sale
        return Answer<bool>.Later;
    }

    [Handle]
    internal static void On(in Answered<Open, bool> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        var sale = request.Read<Opening>().Request;
        request.Set(new ShardAnswers());
        for (int shard = 0; shard < StockShardId.Shards; shard++)
            ctx.Ask(StockShardId.Of(sale.Sale, shard),
                new Restock(sale.Units / StockShardId.Shards + (shard < sale.Units % StockShardId.Shards ? 1 : 0)), a.ReplyTo);
    }

    [Handle]
    internal static void On(in Answered<Restock, bool> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        if (++request.Get<ShardAnswers>().Count == StockShardId.Shards) Done(request, a.ReplyTo, true, in ctx);
    }

    // ---------------------------------------------------------------- buying

    [Handle]
    internal static Answer<OrderStatus> On(in PlaceOrder m, in Context ctx)
    {
        bool known = TryKnow(ctx.World, m.Sale, out var sale);
        if (known && sale.SoldOut) return Outcome(in sale, m.User); // the late crowd: answered here, no actor touched
        var request = Begin(in ctx, out var to).Set(new Placing(m));
        if (known) Place(in m, in sale, request, to, in ctx);
        else ctx.Ask(new SaleId(m.Sale), new Describe(), to);
        return Answer<OrderStatus>.Later;
    }

    /// <summary>The sale described (for a <see cref="PlaceOrder"/> or a <see cref="QueueUp"/>): known from now on, and the request goes on.</summary>
    [Handle]
    internal static void On(in Answered<Describe, SaleInfo> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        if (request.TryGet<Placing>(out var placing))
        {
            Learn(ctx.World, placing.Order.Sale, a.Value);
            Place(placing.Order, a.Value, request, a.ReplyTo, in ctx);
        }
        else if (request.TryGet<QueueingUp>(out var queueing))
        {
            Learn(ctx.World, queueing.Request.Sale, a.Value);
            Join(queueing.Request, a.Value, request, a.ReplyTo, in ctx);
        }
    }

    /// <summary>The sale says whether a position was admitted (for a <see cref="PlaceOrder"/> or a <see cref="CheckIn"/>).</summary>
    [Handle]
    internal static void On(in Answered<Admitted, Admission> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        if (request.TryGet<Placing>(out var placing))
        {
            // A queued sale sells to admitted positions only.
            if (a.Value == Admission.Admitted) ctx.Ask(OrderId.Of(placing.Order.Sale, placing.Order.User), new Buy(), a.ReplyTo);
            else Done(request, a.ReplyTo, OrderStatus.None, in ctx);
        }
        else if (request.TryGet<CheckingIn>(out var checkingIn))
        {
            if (a.Value == Admission.SoldOut) Forget(ctx.World, checkingIn.Request.Sale);
            Done(request, a.ReplyTo, a.Value, in ctx);
        }
    }

    [Handle]
    internal static void On(in Answered<Buy, OrderStatus> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        if (a.Value == OrderStatus.SoldOut) Forget(ctx.World, request.Read<Placing>().Order.Sale); // every shard was empty: ask the sale again, it knows the buyers soon
        Done(request, a.ReplyTo, a.Value, in ctx);
    }

    // ---------------------------------------------------------------- the queue

    [Handle]
    internal static Answer<long> On(in QueueUp m, in Context ctx)
    {
        bool known = TryKnow(ctx.World, m.Sale, out var sale);
        if (known && (sale.SoldOut || !sale.Queued)) return 0;
        var request = Begin(in ctx, out var to).Set(new QueueingUp(m));
        if (known) Join(in m, in sale, request, to, in ctx);
        else ctx.Ask(new SaleId(m.Sale), new Describe(), to);
        return Answer<long>.Later;
    }

    [Handle]
    internal static void On(in Answered<Join, long> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        if (a.Value == 0) Forget(ctx.World, request.Read<QueueingUp>().Request.Sale);
        Done(request, a.ReplyTo, a.Value, in ctx);
    }

    [Handle]
    internal static Answer<Admission> On(in CheckIn m, in Context ctx)
    {
        Begin(in ctx, out var to).Set(new CheckingIn(m));
        ctx.Ask(new SaleId(m.Sale), new Admitted(m.Position), to);
        return Answer<Admission>.Later;
    }

    // ---------------------------------------------------------------- reading the sale

    [Handle]
    internal static Answer<int> On(in SaleStock m, in Context ctx)
    {
        Begin(in ctx, out var to).Set(new ShardAnswers());
        for (int shard = 0; shard < StockShardId.Shards; shard++) ctx.Ask(StockShardId.Of(m.Sale, shard), new UnitsLeft(), to);
        return Answer<int>.Later;
    }

    [Handle]
    internal static void On(in Answered<UnitsLeft, int> a, in Context ctx)
    {
        if (!Resume(a.ReplyTo, a.Status, in ctx, out var request)) return;
        ref var answers = ref request.Get<ShardAnswers>();
        answers.Sum += a.Value;
        if (++answers.Count == StockShardId.Shards) Done(request, a.ReplyTo, answers.Sum, in ctx);
    }

    [Handle]
    internal static Answer<OrderStatus> On(in OrderOf m, in Context ctx)
    {
        ctx.Ask(OrderId.Of(m.Sale, m.User), new GetOrder(), ctx.ReplyLater());
        return Answer<OrderStatus>.Later;
    }

    [Handle]
    internal static void On(in Answered<GetOrder, OrderStatus> a, in Context ctx)
    {
        if (a.Ok) ctx.Reply(a.ReplyTo, a.Value);
        else ctx.Fail(a.ReplyTo, a.Status);
    }

    // ---------------------------------------------------------------- steps

    /// <summary>A buyer of a sold-out sale: "confirmed" if it bought a unit, else "sold out".</summary>
    private static OrderStatus Outcome(in SaleInfo sale, long user) =>
        Array.BinarySearch(sale.Buyers, user) >= 0 ? OrderStatus.Confirmed : OrderStatus.SoldOut;

    private static void Place(in PlaceOrder m, in SaleInfo sale, Entity request, ReplyTo to, in Context ctx)
    {
        if (sale.SoldOut) Done(request, to, Outcome(in sale, m.User), in ctx);
        else if (sale.Queued) ctx.Ask(new SaleId(m.Sale), new Admitted(m.Position), to); // the sale says whether this position was admitted
        else ctx.Ask(OrderId.Of(m.Sale, m.User), new Buy(), to);
    }

    /// <summary>A place in a queued sale's line; none in a sale sold out or without a line (its buyers buy directly).</summary>
    private static void Join(in QueueUp m, in SaleInfo sale, Entity request, ReplyTo to, in Context ctx)
    {
        if (sale.SoldOut || !sale.Queued) Done(request, to, 0L, in ctx);
        else ctx.Ask(new SaleId(m.Sale), new Join(), to);
    }

    // ---------------------------------------------------------------- the sales a shard knows

    private static bool TryKnow(World world, long sale, out SaleInfo info)
    {
        if (world.Index<KnownSale, long>().TryFind(sale, out var known))
        {
            info = known.Read<KnownSale>().Info;
            return true;
        }

        info = default;
        return false;
    }

    private static void Learn(World world, long sale, in SaleInfo info)
    {
        if (world.Index<KnownSale, long>().TryFind(sale, out var known)) known.Set(new KnownSale(sale, info));
        else world.CreateEntity(new KnownSale(sale, info));
    }

    /// <summary>Ask the sale again next time (it sold out since, or the answer would change).</summary>
    private static void Forget(World world, long sale)
    {
        if (world.Index<KnownSale, long>().TryFind(sale, out var known)) known.Destroy();
    }

    // ---------------------------------------------------------------- requests served in steps

    /// <summary>
    /// The request being served, as an entity of the shard's world holding what its next steps need: the waiting
    /// request itself when a caller waits (its reply token names it; answering it, or its caller giving up, removes
    /// it and what the gate put on it), else one of its own for a request sent one-way, removed once served.
    /// <paramref name="to"/> travels with every ask the request makes, and comes back with each answer.
    /// </summary>
    private static Entity Begin(in Context ctx, out ReplyTo to)
    {
        to = ctx.ReplyLater();
        if (to.Expected && ctx.World.TryGetEntity(to.Pending, out var request)) return request;
        request = ctx.World.CreateEntity();
        to = new ReplyTo(request.Id, 0); // nobody to answer: the token only names the entity
        return request;
    }

    /// <summary>
    /// The request an answer belongs to, unless its caller gave up meanwhile (the entity is gone: nothing more to do)
    /// or the ask failed (the request fails with it).
    /// </summary>
    private static bool Resume(ReplyTo to, RpcStatus status, in Context ctx, out Entity request)
    {
        if (!ctx.World.TryGetEntity(to.Pending, out request)) return false;
        if (status == RpcStatus.Ok) return true;
        if (!ctx.Fail(to, status)) request.Destroy();
        return false;
    }

    private static void Done<T>(Entity request, ReplyTo to, in T value, in Context ctx)
    {
        if (!ctx.Reply(to, in value)) request.Destroy(); // a one-way request's own entity (a replied one is gone already)
    }
}
