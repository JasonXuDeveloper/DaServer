using System.Diagnostics;
using DaServer;
using DaServer.Client;
using DaServer.Protocol;
using DaServer.Rpc;
using EasyEcs;
using Sample.Bot;
using Sample.Client;
using Sample.Contracts;

// End-to-end load test: N bots run the game client's logic (Sample.Client: login, enter a map, prediction, pushes into
// the client's world), with BotInputSystem at the controls. One client loop per core, many players per loop.
//
// Usage: Sample.Bot [bots=1000] [seconds=30] [maps=bots/100] [turns/s=2] [chat every s=10] [max p99 ms=250] (0 = off)
// The entry addresses come from DaServer__ClientEndpoints__{i} (set by the AppHost's WithClientEndpoints); default 127.0.0.1:7100.
// Exit code 0 only if every bot came online and, during the measured window, every bot received every realm broadcast
// in order (no gap), no bot disconnected, and the p99 broadcast latency stayed under the limit.

var arg = (int i, int fallback) => args.Length > i ? int.Parse(args[i]) : fallback;
int bots = arg(0, 1000);
int seconds = arg(1, 30);
int maps = arg(2, Math.Max(1, bots / 100));
int turnsPerSecond = arg(3, 2);
int chatEverySeconds = arg(4, 10);
int maxP99Ms = arg(5, 250);
var entries = new List<System.Net.DnsEndPoint>();
for (int i = 0; Environment.GetEnvironmentVariable($"DaServer__ClientEndpoints__{i}") is { } address; i++)
    entries.Add(new System.Net.DnsEndPoint(address[..address.LastIndexOf(':')], int.Parse(address[(address.LastIndexOf(':') + 1)..])));
if (entries.Count == 0) entries.Add(new System.Net.DnsEndPoint("127.0.0.1", 7100));

var loops = new ClientLoop[Math.Min(Environment.ProcessorCount, bots)];
for (int i = 0; i < loops.Length; i++)
{
    loops[i] = new ClientLoop("bots-" + i);
    loops[i].Add<BotInputSystem>();
    loops[i].Add<MeasureSystem>();
}

var clients = new ClusterClient[bots];
var online = Stopwatch.StartNew();
await Parallel.ForAsync(0, bots, new ParallelOptions { MaxDegreeOfParallelism = 256 }, async (i, _) =>
{
    var client = clients[i] = new ClusterClient(callbackQueue: 1024);
    client.Disconnected += _ => Interlocked.Increment(ref Stats.Disconnects);
    await Retry(async () =>
    {
        await client.ConnectAsync(entries);
        return true;
    }, static e => e is not RpcException);

    await loops[i % loops.Length].Run(async () =>
    {
        var world = SynchronizationContext.Current is ClientLoop loop ? loop.World : throw new InvalidOperationException();
        var player = Player.Create(world, client);
        if (!await Retry(() => Player.LogInAsync(player, "bot-" + i), Starting)) throw new InvalidOperationException("login failed for bot " + i);
        // The client-facing node routes this to the player bound at login, and the map binds the session for input.
        await Retry(() => Player.EnterMapAsync(player, new MapId(1 + i % maps)), Starting);
        int turnEvery = turnsPerSecond > 0 ? Math.Max(1, Timing.TickRate / turnsPerSecond) : 0;
        player.Add<BotInput>() = new BotInput((uint)i * 2654435761u | 1, turnEvery, chatEverySeconds * Timing.TickRate, i);
        player.Add<Measured>();
        return true;
    });
});
Console.WriteLine($"{bots} bots online in {online.Elapsed.TotalSeconds:F1}s ({maps} maps, {loops.Length} client loops)");

// Warm-up: every bot receives a realm broadcast before the measured window starts.
var warmup = Stopwatch.StartNew();
while (await Sum(Waiting) > 0 && warmup.Elapsed.TotalSeconds < 30)
    await Task.Delay(100);
Stats.Start();

var clock = Stopwatch.StartNew();
var window = Stats.Latency.Snapshot();
long updatesBefore = await Sum(Updates), linesBefore = await Sum(Lines);
var last = TimeSpan.Zero;
while (clock.Elapsed.TotalSeconds < seconds)
{
    await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds - clock.Elapsed.TotalSeconds)));
    var now = Stats.Latency.Snapshot();
    var interval = window.Until(now);
    window = now;
    long updates = await Sum(Updates), lines = await Sum(Lines);
    double elapsed = (clock.Elapsed - last).TotalSeconds;
    Console.WriteLine($"t={clock.Elapsed.TotalSeconds,4:F0}s realm/s={interval.Count / elapsed,8:F0} " +
                      $"p50={interval.Percentile(0.50),6:F1}ms p99={interval.Percentile(0.99),6:F1}ms max={interval.Max,6:F1}ms " +
                      $"positions/s={(updates - updatesBefore) / elapsed,8:F0} chat/s={(lines - linesBefore) / elapsed,8:F0} " +
                      $"gaps={Interlocked.Read(ref Stats.Gaps)} disconnects={Interlocked.Read(ref Stats.Disconnects)}");
    (updatesBefore, linesBefore, last) = (updates, lines, clock.Elapsed);
}

// Results of the measured window.
Stats.Measuring = false;
var latency = Stats.Latency.Snapshot();
long silent = await Sum(Silent);
long positions = await Sum(Updates);
long disconnects = Interlocked.Read(ref Stats.Disconnects);
await Parallel.ForEachAsync(clients, static (c, _) => c.DisposeAsync());
foreach (var loop in loops) loop.Dispose();

double p99 = latency.Percentile(0.99);
Console.WriteLine($"realm broadcasts: {latency.Count} delivered, " +
                  $"latency p50={latency.Percentile(0.50):F1}ms p99={p99:F1}ms p99.9={latency.Percentile(0.999):F1}ms max={latency.Max:F1}ms");
var failures = new List<string>();
if (silent > 0) failures.Add($"{silent} bots received no realm broadcast while measured");
if (turnsPerSecond > 0 && positions == 0) failures.Add("no position update reached any bot");
if (Stats.Gaps > 0) failures.Add($"{Stats.Gaps} realm broadcasts missed or out of order");
if (disconnects > 0) failures.Add($"{disconnects} bots disconnected");
if (p99 > maxP99Ms) failures.Add($"p99 latency {p99:F1}ms > {maxP99Ms}ms");
Console.WriteLine(failures.Count == 0 ? "E2E OK" : "E2E FAILED: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

// Sum a per-world count over every loop (each read on its loop's thread).
async Task<long> Sum(Func<World, long> count)
{
    long total = 0;
    foreach (var loop in loops) total += await loop.Run(() => Task.FromResult(count(loop.World)));
    return total;
}

static long Waiting(World world)
{
    long total = 0;
    foreach (var item in world.Query<Realm>()) total += item.Component1.Received == 0 ? 1 : 0;
    return total;
}

static long Silent(World world)
{
    long total = 0;
    foreach (var item in world.Query<Measured>()) total += item.Component1.InWindow == 0 ? 1 : 0;
    return total;
}

static long Updates(World world)
{
    long total = 0;
    foreach (var item in world.Query<View>()) total += item.Component1.Updates;
    return total;
}

static long Lines(World world)
{
    long total = 0;
    foreach (var item in world.Query<Heard>()) total += item.Component1.Lines;
    return total;
}

// The cluster may still be starting or converging: the client-facing node has not met the node hosting the target yet
// (NotHosted), or a node still warming up on a busy machine misses the call's deadline (Timeout). Logging in and
// entering a map again are idempotent.
static bool Starting(Exception e) => e is RpcException { Status: RpcStatus.NotHosted or RpcStatus.Timeout };

static async Task<T> Retry<T>(Func<Task<T>> call, Func<Exception, bool> transient)
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
