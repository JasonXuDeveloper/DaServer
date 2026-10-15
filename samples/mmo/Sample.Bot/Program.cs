using System.Diagnostics;
using DaServer;
using DaServer.Client;
using DaServer.Protocol;
using DaServer.Rpc;
using Sample.Contracts;

// End-to-end load test: N bots connect through the gateways' addresses (and are directed to the less loaded gateway),
// log in, enter a map, then move and chat while the realm broadcasts its status to every session every few ticks.
//
// Usage: Sample.Bot [bots=1000] [seconds=30] [maps=bots/100] [moves/s=2] [chat every s=10] [max p99 ms=250] (0 = off)
// The entry addresses come from DaServer__ClientEndpoints__{i} (set by the AppHost's WithClientEndpoints); default 127.0.0.1:7100.
// Exit code 0 only if every bot came online and, during the measured window, every bot received every realm broadcast
// in order (no gap), no bot disconnected, and the p99 broadcast latency stayed under the limit.

var arg = (int i, int fallback) => args.Length > i ? int.Parse(args[i]) : fallback;
int bots = arg(0, 1000);
int seconds = arg(1, 30);
int maps = arg(2, Math.Max(1, bots / 100));
int movesPerSecond = arg(3, 2);
int chatEverySeconds = arg(4, 10);
int maxP99Ms = arg(5, 250);
var entries = new List<System.Net.DnsEndPoint>();
for (int i = 0; Environment.GetEnvironmentVariable($"DaServer__ClientEndpoints__{i}") is { } address; i++)
    entries.Add(new System.Net.DnsEndPoint(address[..address.LastIndexOf(':')], int.Parse(address[(address.LastIndexOf(':') + 1)..])));
if (entries.Count == 0) entries.Add(new System.Net.DnsEndPoint("127.0.0.1", 7100));

var stats = new Stats();
var players = new Bot[bots];
var online = Stopwatch.StartNew();
await Parallel.ForAsync(0, bots, new ParallelOptions { MaxDegreeOfParallelism = 256 }, async (i, _) =>
{
    var client = new ClusterClient();
    var bot = players[i] = new Bot(client, stats);
    client.Register(bot);
    client.Disconnected += _ => Interlocked.Increment(ref stats.Disconnects);
    await Retry(() => client.ConnectAsync(entries), static e => e is not RpcException);
    var login = await RetryFor(() => client.Ask(new Login("bot-" + i)), Starting);
    if (!login.Ok) throw new InvalidOperationException("login failed for bot " + i);
    // The gateway routes this to the player bound at login, and the map binds the session for input.
    await RetryFor(() => client.Ask(new EnterMap(new MapId(1 + i % maps))), Starting);
});
Console.WriteLine($"{bots} bots online in {online.Elapsed.TotalSeconds:F1}s ({maps} maps)");

// Warm-up: every bot receives a realm broadcast before the measured window starts.
var warmup = Stopwatch.StartNew();
while (Array.Exists(players, static p => Volatile.Read(ref p.LastTick) == 0) && warmup.Elapsed.TotalSeconds < 30)
    await Task.Delay(100);
stats.Reset();
foreach (var player in players) player.StartMeasuring();

var random = new Random(1);
var clock = Stopwatch.StartNew();
var report = TimeSpan.FromSeconds(5);
var lastReport = TimeSpan.Zero;
var window = new Histogram();
long round = 0, movesBefore = 0, chatsBefore = 0;
while (clock.Elapsed.TotalSeconds < seconds)
{
    // Input rounds at 10 Hz: each bot moves movesPerSecond times a second and chats every chatEverySeconds.
    var started = clock.Elapsed;
    for (int i = 0; i < players.Length; i++)
    {
        var bot = players[i];
        if (movesPerSecond > 0 && (round + i) % Math.Max(1, 10 / movesPerSecond) == 0)
            bot.Client.Send(new Move((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1)));
        if (chatEverySeconds > 0 && (round + i) % (10 * chatEverySeconds) == 0) bot.Client.Send(new Say("hello"));
    }

    round++;
    if (clock.Elapsed - lastReport >= report)
    {
        var snapshot = stats.Latency.Snapshot();
        window.Subtract(snapshot, out var interval);
        window = snapshot;
        double elapsed = (clock.Elapsed - lastReport).TotalSeconds;
        long moves = -movesBefore, chats = -chatsBefore;
        foreach (var p in players)
        {
            moves += Volatile.Read(ref p.Moves);
            chats += Volatile.Read(ref p.Chats);
        }

        movesBefore += moves;
        chatsBefore += chats;

        Console.WriteLine($"t={clock.Elapsed.TotalSeconds,4:F0}s realm/s={interval.Count / elapsed,8:F0} " +
                          $"p50={interval.Percentile(0.50),6:F1}ms p99={interval.Percentile(0.99),6:F1}ms max={interval.Max,6:F1}ms " +
                          $"positions/s={moves / elapsed,8:F0} chat/s={chats / elapsed,8:F0} " +
                          $"gaps={stats.Gaps} disconnects={stats.Disconnects}");
        lastReport = clock.Elapsed;
    }

    var wait = TimeSpan.FromMilliseconds(100) - (clock.Elapsed - started);
    if (wait > TimeSpan.Zero) await Task.Delay(wait);
}

// Results of the measured window.
var latency = stats.Latency.Snapshot();
long minReceived = long.MaxValue, positions = 0;
foreach (var player in players)
{
    minReceived = Math.Min(minReceived, player.Received);
    positions += Volatile.Read(ref player.Moves);
}

long disconnects = Interlocked.Read(ref stats.Disconnects);
await Parallel.ForEachAsync(players, static (p, _) => p.Client.DisposeAsync());

double p99 = latency.Percentile(0.99);
Console.WriteLine($"realm broadcasts: {latency.Count} delivered, every bot received >= {minReceived}, " +
                  $"latency p50={latency.Percentile(0.50):F1}ms p99={p99:F1}ms p99.9={latency.Percentile(0.999):F1}ms max={latency.Max:F1}ms");
var failures = new List<string>();
if (minReceived == 0) failures.Add("a bot received no realm broadcast");
if (movesPerSecond > 0 && positions == 0) failures.Add("no position update reached any bot");
if (stats.Gaps > 0) failures.Add($"{stats.Gaps} realm broadcasts missed or out of order");
if (disconnects > 0) failures.Add($"{disconnects} bots disconnected");
if (p99 > maxP99Ms) failures.Add($"p99 latency {p99:F1}ms > {maxP99Ms}ms");
Console.WriteLine(failures.Count == 0 ? "E2E OK" : "E2E FAILED: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

// The cluster may still be starting or converging: the gateway has not met the node hosting the target yet
// (NotHosted), or a node still warming up on a busy machine misses the call's deadline (Timeout). Logging in and
// entering a map again are idempotent.
static bool Starting(Exception e) => e is RpcException { Status: RpcStatus.NotHosted or RpcStatus.Timeout };

static async Task<T> RetryFor<T>(Func<ValueTask<T>> call, Func<Exception, bool> transient)
{
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (attempt < 60 && transient(e))
        {
            await Task.Delay(500);
        }
    }
}

static Task Retry(Func<ValueTask> call, Func<Exception, bool> transient) =>
    RetryFor(async () =>
    {
        await call();
        return true;
    }, transient);

/// <summary>One bot: its connection and what it received (pushes arrive on IO threads).</summary>
sealed class Bot(ClusterClient client, Stats stats)
{
    public readonly ClusterClient Client = client;
    public long LastTick;
    public long Received;
    private bool _measuring;

    public void StartMeasuring()
    {
        Volatile.Write(ref Received, 0);
        Volatile.Write(ref _measuring, true);
    }

    [Handle]
    public void On(RealmStatus status)
    {
        long previous = LastTick;
        LastTick = status.Tick;
        if (!Volatile.Read(ref _measuring)) return;
        Received++;
        if (status.Tick != previous + 5 && Interlocked.Increment(ref stats.Gaps) <= 5) // RealmBroadcastSystem.EveryTicks
            Console.WriteLine($"gap: realm tick {previous} then {status.Tick}");
        stats.Latency.Add(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - status.ServerTimeTicks).TotalMilliseconds);
    }

    // Totals per bot (one connection: one IO thread at a time), summed by the report: a shared counter would be the hottest
    // contended cache line of the process at 200k snapshots per second.
    public long Moves, Chats;

    [Handle] public void On(Motion m) => Moves += m.Avatars.Count;

    [Handle] public void On(Gone _) { }

    [Handle] public void On(Chat _) => Chats++;
}

sealed class Stats
{
    public readonly Histogram Latency = new();
    public long Gaps, Disconnects;

    public void Reset()
    {
        Latency.Clear();
        Gaps = 0;
    }
}

/// <summary>Lock-free latency histogram: 0.1 ms buckets up to 5 s.</summary>
sealed class Histogram
{
    private const double BucketMs = 0.1;
    private readonly long[] _buckets = new long[50_001];

    public long Count { get; private set; }
    public double Max { get; private set; }

    public void Add(double ms) =>
        Interlocked.Increment(ref _buckets[Math.Clamp((int)(ms / BucketMs), 0, _buckets.Length - 1)]);

    public void Clear() => Array.Clear(_buckets);

    public Histogram Snapshot()
    {
        var copy = new Histogram();
        for (int i = 0; i < _buckets.Length; i++) copy._buckets[i] = Volatile.Read(ref _buckets[i]);
        copy.Summarize();
        return copy;
    }

    /// <summary>What <paramref name="later"/> recorded after this snapshot.</summary>
    public void Subtract(Histogram later, out Histogram interval)
    {
        interval = new Histogram();
        for (int i = 0; i < _buckets.Length; i++) interval._buckets[i] = later._buckets[i] - _buckets[i];
        interval.Summarize();
    }

    public double Percentile(double p)
    {
        long target = (long)Math.Ceiling(Count * p), seen = 0;
        for (int i = 0; i < _buckets.Length; i++)
            if ((seen += _buckets[i]) >= target && target > 0)
                return (i + 1) * BucketMs;
        return 0;
    }

    private void Summarize()
    {
        Count = 0;
        Max = 0;
        for (int i = 0; i < _buckets.Length; i++)
        {
            Count += _buckets[i];
            if (_buckets[i] != 0) Max = (i + 1) * BucketMs;
        }
    }
}
