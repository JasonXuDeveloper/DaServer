using DaServer;
using DaServer.Client;
using DaServer.Protocol;
using DaServer.Rpc;
using DaServer.Simulation;
using DaServer.Simulation.Planning;
using EasyEcs;
using Sample.Bot;
using Sample.Client;
using Sample.Contracts;

// End-to-end load test: a swarm of simulated players (DaServer.Simulation) runs the game client's logic
// (Sample.Client: login, enter a map, prediction, pushes into the client's world) with a planner at the controls
// (Behavior: change maps, chat, wander). One client loop per core, many players per loop.
//
// Usage: Sample.Bot [bots=1000] [seconds=30] [maps=bots/100] [turns/s=2] [chat every s=10] [max p99 ms=250]
//                   [change map every s=0] (0 = off)
// The entry addresses come from DaServer__ClientEndpoints__{i} (set by the AppHost's WithClientEndpoints); default 127.0.0.1:7100.
// Exit code 0 only if every bot came online and, during the measured window, every bot received every realm broadcast
// in order (no gap), no bot disconnected, every map change succeeded and the p99 broadcast latency stayed under the limit.

var arg = (int i, int fallback) => args.Length > i ? int.Parse(args[i]) : fallback;
int bots = arg(0, 1000);
int seconds = arg(1, 30);
int maps = arg(2, Math.Max(1, bots / 100));
int turnsPerSecond = arg(3, 2);
int chatEverySeconds = arg(4, 10);
int maxP99Ms = arg(5, 250);
int travelEverySeconds = arg(6, 0);
var entries = new List<System.Net.DnsEndPoint>();
for (int i = 0; Environment.GetEnvironmentVariable($"DaServer__ClientEndpoints__{i}") is { } address; i++)
    entries.Add(new System.Net.DnsEndPoint(address[..address.LastIndexOf(':')], int.Parse(address[(address.LastIndexOf(':') + 1)..])));
if (entries.Count == 0) entries.Add(new System.Net.DnsEndPoint("127.0.0.1", 7100));

var domain = Behavior.Domain();
await using var swarm = new Swarm(new SwarmOptions
{
    Loops = Math.Min(Environment.ProcessorCount, bots),
    TickRate = Timing.TickRate,
    OnPass = static world => world.Update<ClientPushes>(), // pushes as they arrive
    Configure = (world, meter) =>
    {
        world.AddSingleton<HtnDomain>() = domain;
        world.AddSingleton<BotMeters>() = BotMeters.Of(meter);
        ClientSystems.AddTo(world);
        Behavior.AddSystems(world);
        world.AddSystem<MeasureSystem>();
    },
});

var online = System.Diagnostics.Stopwatch.StartNew();
await swarm.StartAsync(bots, static () => new ClusterClient(callbackQueue: 1024), async user =>
{
    int i = user.Index;
    await Swarm.Retry(async () =>
    {
        await user.Client.ConnectAsync(entries);
        return true;
    }, static e => e is not RpcException);
    var player = Player.Create(user.World, user.Client);
    if (!await Swarm.Retry(() => Player.LogInAsync(player, "bot-" + i), Starting)) throw new InvalidOperationException("login failed for bot " + i);
    // The client-facing node routes this to the player bound at login, and the map binds the session for input.
    await Swarm.Retry(() => Player.EnterMapAsync(player, new MapId(1 + i % maps)), Starting);
    // Bots start apart (their schedules spread over a second), so their input does not come in waves.
    player.Add<Bot>() = new Bot((uint)i * 2654435761u | 1, turnsPerSecond > 0 ? 1000 / turnsPerSecond : 0,
        chatEverySeconds * 1000, travelEverySeconds * 1000, maps, Environment.TickCount64 + i % 1000);
    player.Add<Facts>();
    player.Add<Plan>() = new Plan();
    player.Add<Measured>();
});
Console.WriteLine($"{bots} bots online in {online.Elapsed.TotalSeconds:F1}s ({maps} maps, {swarm.Loops.Count} client loops)");

// Warm-up: every bot receives a realm broadcast before the measured window starts.
await swarm.WaitAsync(static w => Count<Realm>(w, static r => r.Received == 0), TimeSpan.FromSeconds(30));

long updatesBefore = await swarm.SumAsync(Updates), linesBefore = await swarm.SumAsync(Lines);
var elapsed = TimeSpan.Zero;
var cost = await swarm.MeasureAsync(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(5), async (latency, interval) =>
{
    long updates = await swarm.SumAsync(Updates), lines = await swarm.SumAsync(Lines);
    double s = interval.TotalSeconds;
    elapsed += interval;
    Console.WriteLine($"t={elapsed.TotalSeconds,4:F0}s realm/s={latency.Count / s,8:F0} " +
                      $"p50={latency.Percentile(0.50),6:F1}ms p99={latency.Percentile(0.99),6:F1}ms max={latency.Max,6:F1}ms " +
                      $"positions/s={(updates - updatesBefore) / s,8:F0} chat/s={(lines - linesBefore) / s,8:F0} " +
                      string.Join(" ", swarm.Meter.Counters));
    (updatesBefore, linesBefore) = (updates, lines);
});

// Results of the measured window.
var all = swarm.Meter.Latency.Snapshot();
long silent = await swarm.SumAsync(static w => Count<Measured>(w, static m => m.InWindow == 0));
long positions = await swarm.SumAsync(Updates);
double p99 = all.Percentile(0.99);
Console.WriteLine($"bot process while measured: {cost}");
Console.WriteLine($"realm broadcasts: {all.Count} delivered, " +
                  $"latency p50={all.Percentile(0.50):F1}ms p99={p99:F1}ms p99.9={all.Percentile(0.999):F1}ms max={all.Max:F1}ms");
var failures = new List<string>();
if (silent > 0) failures.Add($"{silent} bots received no realm broadcast while measured");
if (turnsPerSecond > 0 && positions == 0) failures.Add("no position update reached any bot");
foreach (var counter in swarm.Meter.Counters)
    if (counter.Value > 0) failures.Add(counter.ToString());
if (p99 > maxP99Ms) failures.Add($"p99 latency {p99:F1}ms > {maxP99Ms}ms");
Console.WriteLine(failures.Count == 0 ? "E2E OK" : "E2E FAILED: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

static long Count<T>(World world, Func<T, bool> matches) where T : struct, IComponent
{
    long n = 0;
    foreach (var item in world.Query<T>())
        if (matches(item.Component1)) n++;
    return n;
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
// (NotHosted), a node still warming up on a busy machine misses the call's deadline (Timeout), or thousands arriving
// at once fill a map's mailbox (Overloaded: refused, nothing ran). Logging in and entering a map again are idempotent.
static bool Starting(Exception e) => e is RpcException { Status: RpcStatus.NotHosted or RpcStatus.Timeout or RpcStatus.Overloaded };
