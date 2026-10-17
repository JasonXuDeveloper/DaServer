using System.Collections.Concurrent;
using ConsoleAppFramework;
using DaServer.Client;
using DaServer;
using DaServer.Simulation;
using FlashSale.Contracts;

// The flash sale's load test and end-to-end test: a crowd of buyers (DaServer.Simulation) over the client protocol. The
// entry addresses come from Aspire's WithClientEndpoints.
await ConsoleApp.RunAsync(args, Crowd.RunAsync);

internal static class Crowd
{
    /// <summary>
    /// Buyers arrive at once for the units on sale: each buys and buys again (a retry: the answer must not change); in a
    /// queued sale (<paramref name="perSecond"/> above 0) each first queues and waits until admitted or told the sale is
    /// sold out. Exit code 0 only if exactly the units were sold, none is left, no retry changed its answer and every
    /// confirmed order reads back confirmed.
    /// </summary>
    /// <param name="buyers">Buyers in the crowd.</param>
    /// <param name="units">Units on sale.</param>
    /// <param name="perSecond">A queued sale admitting this many buyers a second (0: no queue, everybody buys at once).</param>
    /// <param name="connections">Client connections the buyers share (a buyer is a flow, not a socket).</param>
    /// <param name="inFlight">Buyers in progress per connection.</param>
    /// <param name="pollMs">How often a waiting buyer asks whether it is admitted.</param>
    /// <param name="flushMs">Requests leave each connection on this beat (<c>ClusterClient.FlushInterval</c>; 0: at once).</param>
    /// <param name="webSocket">Connect over WebSocket (ws://host:port/clients/ws), as browsers do, instead of TCP.</param>
    /// <param name="sale">The sale's number.</param>
    public static async Task<int> RunAsync(int buyers = 1_000_000, int units = 100, int perSecond = 0, int connections = 64,
        int inFlight = 256, int pollMs = 500, int flushMs = 1, string webSocket = "", long sale = 1)
    {
        var endpoints = Endpoints.FromEnvironment();
        await using var swarm = new Swarm(new SwarmOptions { Loops = Math.Min(Environment.ProcessorCount, connections) });
        var meter = swarm.Meter;
        var (soldOut, turnedAway, soldOutWaiting, requests) = (meter.Counter("sold out"), meter.Counter("turned away from the queue"),
            meter.Counter("sold out while queued"), meter.Counter("requests", SwarmCounterKind.Rate));
        var changed = meter.Counter("retries answered differently", SwarmCounterKind.Failure);
        var confirmed = new ConcurrentDictionary<long, bool>();
        var clients = new ClusterClient[connections];
        await swarm.StartAsync(connections, () => new ClusterClient(maxPendingCalls: inFlight * 2) { FlushInterval = TimeSpan.FromMilliseconds(flushMs) }, async user =>
        {
            clients[user.Index] = user.Client;
            if (webSocket.Length > 0) await Swarm.Retry(async () => { await user.Client.ConnectAsync(new Uri(webSocket)); return true; });
            else await user.ConnectAsync(endpoints);
        });
        await Call(() => clients[0].Ask(new OpenSale(sale, units, perSecond)));
        Console.WriteLine($"sale {sale}: {units} units, {buyers:N0} buyers{(perSecond > 0 ? $" queued, admitted {perSecond}/s" : "")}, " +
                          $"{connections} connections x {inFlight} in flight");

        long next = 0;
        var lanes = new List<Task>();
        foreach (var client in clients)
            for (int i = 0; i < inFlight; i++)
                lanes.Add(Task.Run(async () =>
                {
                    for (long user; (user = Interlocked.Increment(ref next)) <= buyers;) await Buyer(client, user);
                }));
        var result = await swarm.MeasureUntilAsync(Task.WhenAll(lanes), TimeSpan.FromSeconds(5));

        // The sale's guarantees, read back from the cluster.
        int left = await Call(() => clients[0].Ask(new SaleStock(sale)));
        int unconfirmed = 0;
        foreach (long user in confirmed.Keys)
            if (await Call(() => clients[user % connections].Ask(new OrderOf(sale, user))) != OrderStatus.Confirmed) unconfirmed++;
        Console.WriteLine($"{buyers:N0} buyers: {confirmed.Count} confirmed, {soldOut.Value:N0} sold out, {turnedAway.Value:N0} turned away from " +
                          $"the queue, {soldOutWaiting.Value:N0} sold out while queued; {requests.Value:N0} requests in " +
                          $"{result.Cost.Elapsed.TotalSeconds:F1}s ({requests.Value / result.Cost.Elapsed.TotalSeconds:N0}/s)");
        return result
            .Expect(confirmed.Count == Math.Min(units, buyers), $"{confirmed.Count} orders confirmed, not {Math.Min(units, buyers)}")
            .Expect(left == Math.Max(0, units - buyers), $"{left} units left")
            .Expect(unconfirmed == 0, $"{unconfirmed} confirmed orders read back otherwise")
            .Report();

        async Task Buyer(ClusterClient client, long user)
        {
            long position = 0;
            if (perSecond > 0)
            {
                position = await Call(() => client.Ask(new QueueUp(sale)));
                if (position == 0)
                {
                    turnedAway.Add();
                    return;
                }

                Admission admission;
                while ((admission = await Call(() => client.Ask(new CheckIn(sale, position)))) == Admission.Waiting) await Task.Delay(pollMs);
                if (admission == Admission.SoldOut)
                {
                    soldOutWaiting.Add();
                    return;
                }
            }

            var status = await Call(() => client.Ask(new PlaceOrder(sale, user, position)));
            if (await Call(() => client.Ask(new PlaceOrder(sale, user, position))) != status) changed.Add();
            if (status == OrderStatus.Confirmed) confirmed[user] = true;
            else soldOut.Add();
        }

        // One request: its latency recorded, retried while the cluster asks to come back later.
        async Task<T> Call<T>(Func<Reply<T>> ask) => await Swarm.Retry(async () =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var reply = await ask();
            meter.Latency.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            requests.Add();
            return reply;
        });
    }
}
