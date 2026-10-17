// A flash sale's crowd: Users buyers arrive at once for Units units. Each joins the queue, polls until admitted or told
// the sale is sold out, buys if admitted and retries the purchase once (the answer must not change). At the end: exactly
// Units confirmed orders, no stock left, every confirmed order readable. Prints "FLASHSALE OK" and exits 0 when all hold.
//   dotnet run --project samples/flashsale/FlashSale.Load -- --Urls:0 http://localhost:5000 --Users 1000000 --Units 100
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build().GetSection("Sale");
string[] urls = config.GetSection("Urls").Get<string[]>() ?? ["http://localhost:5000"];
long sale = config.GetValue("Id", 1L);
int users = config.GetValue("Users", 1_000_000), units = config.GetValue("Units", 100);
int perSecond = config.GetValue("PerSecond", 2_000), concurrency = config.GetValue("Concurrency", 2_000);
var poll = TimeSpan.FromMilliseconds(config.GetValue("PollMs", 500));

using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = Math.Max(1, concurrency / urls.Length),
    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
}) { Timeout = TimeSpan.FromSeconds(30) };
var stats = new Stats();
string Url(long user, string path) => $"{urls[user % urls.Length]}/sales/{sale}/{path}";

(await http.PostAsync($"{urls[0]}/sales/{sale}/open?units={units}&perSecond={perSecond}", null)).EnsureSuccessStatusCode();
Console.WriteLine($"sale {sale}: {units} units, {users:N0} buyers, admitted {perSecond}/s, {concurrency} connections, {urls.Length} nodes");

long next = 0;
var clock = Stopwatch.StartNew();
var report = Task.Run(async () =>
{
    while (Interlocked.Read(ref next) < users)
    {
        await Task.Delay(5000);
        Console.WriteLine($"t={clock.Elapsed.TotalSeconds,5:F0}s {stats.Line()}");
    }
});
var workers = new Task[concurrency];
for (int w = 0; w < workers.Length; w++)
    workers[w] = Task.Run(async () =>
    {
        for (long user; (user = Interlocked.Increment(ref next)) <= users;) await Buyer(user);
    });
await Task.WhenAll(workers);
var elapsed = clock.Elapsed;

// The verdict: the sale's guarantees, read back from the cluster.
var failures = new List<string>();
int left = (await Call<StockReply>(HttpMethod.Get, $"{urls[0]}/sales/{sale}/stock")).Left;
if (stats.Confirmed.Count != Math.Min(units, users)) failures.Add($"{stats.Confirmed.Count} confirmed orders, not {Math.Min(units, users)}");
if (left != Math.Max(0, units - users)) failures.Add($"{left} units left");
if (stats.Changed > 0) failures.Add($"{stats.Changed} retried purchases changed their answer");
foreach (long user in stats.Confirmed.Keys)
    if ((await Call<StatusReply>(HttpMethod.Get, Url(user, $"orders/{user}"))).Status != "Confirmed") failures.Add($"order of {user} not confirmed");
Console.WriteLine($"done in {elapsed.TotalSeconds:F1}s: {stats.Line()}, {stats.Requests / elapsed.TotalSeconds:N0} requests/s");
Console.WriteLine(failures.Count == 0 ? "FLASHSALE OK" : "FLASHSALE FAILED: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

async Task Buyer(long user)
{
    var joined = await Call<QueueReply>(HttpMethod.Post, Url(user, $"queue?user={user}"));
    var queue = joined;
    while (queue.Status == "Waiting")
    {
        await Task.Delay(poll);
        queue = await Call<QueueReply>(HttpMethod.Get, Url(user, $"queue?user={user}&ticket={joined.Ticket}"));
    }

    if (queue.Status == "SoldOut")
    {
        Interlocked.Increment(ref joined.Status == "SoldOut" ? ref stats.TurnedAway : ref stats.SoldOutWaiting);
        return;
    }

    string buy = Url(user, $"buy?user={user}&pass={queue.Pass}");
    string status = (await Call<StatusReply>(HttpMethod.Post, buy)).Status;
    if ((await Call<StatusReply>(HttpMethod.Post, buy)).Status != status) Interlocked.Increment(ref stats.Changed); // a retry
    if (status == "Confirmed") stats.Confirmed[user] = true;
    else Interlocked.Increment(ref stats.SoldOutAdmitted);
}

// One request, retried while the cluster asks to come back later (429, 503, 504); its latency is recorded.
async Task<T> Call<T>(HttpMethod method, string url)
{
    for (int attempt = 0; ; attempt++)
    {
        long start = Stopwatch.GetTimestamp();
        using var response = await http.SendAsync(new HttpRequestMessage(method, url));
        stats.Record(Stopwatch.GetElapsedTime(start));
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>())!;
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) || attempt == 20)
            throw new HttpRequestException($"{method} {url}: {(int)response.StatusCode}");
        Interlocked.Increment(ref stats.Retries);
        await Task.Delay(100 << Math.Min(attempt, 4));
    }
}

internal sealed record QueueReply(string Status, string? Ticket, string? Pass);

internal sealed record StatusReply(string Status);

internal sealed record StockReply(int Left);

internal sealed class Stats
{
    public readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> Confirmed = new();
    public long TurnedAway, SoldOutWaiting, SoldOutAdmitted, Changed, Retries, Requests;
    private readonly long[] _latencyBuckets = new long[64]; // power-of-two microsecond buckets

    public void Record(TimeSpan latency)
    {
        Interlocked.Increment(ref Requests);
        Interlocked.Increment(ref _latencyBuckets[63 - long.LeadingZeroCount(Math.Max(1, (long)latency.TotalMicroseconds))]);
    }

    private double Percentile(double p)
    {
        long total = 0, seen = 0;
        foreach (long count in _latencyBuckets) total += count;
        for (int i = 0; i < _latencyBuckets.Length; i++)
            if ((seen += _latencyBuckets[i]) >= total * p) return (1L << (i + 1)) / 1000.0;
        return 0;
    }

    public string Line() =>
        $"confirmed={Confirmed.Count} turned away={TurnedAway:N0} sold out while waiting={SoldOutWaiting:N0} sold out at purchase={SoldOutAdmitted:N0} " +
        $"requests={Requests:N0} retries={Retries:N0} latency p50<{Percentile(0.5):F1}ms p99<{Percentile(0.99):F1}ms";
}
