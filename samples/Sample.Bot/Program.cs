using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Client;
using Sample.Contracts;

// Load generator / end-to-end client: N bots log in, enter maps, move at 10 Hz and chat, while counting pushes.
// Usage: Sample.Bot [bots=100] [seconds=30] [maps=10]; gateways come from DaServer__Gateways__{i} (set by the AppHost's
// WithGateways) and default to 127.0.0.1:7100.
// Exit code 0 only if every bot logged in and received broadcasts (used as the e2e check).

int bots = args.Length > 0 ? int.Parse(args[0]) : 100;
int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
int maps = args.Length > 2 ? int.Parse(args[2]) : 10;
var gateways = new List<string>();
while (Environment.GetEnvironmentVariable($"DaServer__Gateways__{gateways.Count}") is { } gateway) gateways.Add(gateway);
if (gateways.Count == 0) gateways.Add("127.0.0.1:7100");

var inbox = new Inbox();
var clients = new List<ClusterClient>();
var inputs = new List<MapInputProxy>();

for (int i = 0; i < bots; i++)
{
    var client = new ClusterClient();
    await ConnectWithRetry(client, gateways[i % gateways.Count]);
    client.Register<IPlayerClient>(inbox);

    var login = await client.Account().Login("bot-" + i);
    if (!login.Ok) throw new InvalidOperationException("login failed for bot " + i);
    await client.Player().EnterMap(1 + i % maps); // the gateway routes to the player bound at login
    clients.Add(client);
    inputs.Add(client.MapInput());                // and this to the map the player entered
}

Console.WriteLine($"{bots} bots online on {gateways.Count} gateway(s), {maps} maps");

var random = new Random(1);
var clock = Stopwatch.StartNew();
long lastMoved = 0;
while (clock.Elapsed.TotalSeconds < seconds)
{
    var tick = Stopwatch.StartNew();
    foreach (var input in inputs)
        input.Move((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
    if (random.Next(10) == 0) inputs[random.Next(inputs.Count)].Chat("hello");

    if ((int)clock.Elapsed.TotalSeconds != (int)(clock.Elapsed.TotalSeconds - tick.Elapsed.TotalSeconds))
    {
        long moved = Interlocked.Read(ref inbox.Moved);
        Console.WriteLine($"t={clock.Elapsed.TotalSeconds:F0}s pushes/s={moved - lastMoved} chat={inbox.Chat}");
        lastMoved = moved;
    }

    var wait = TimeSpan.FromMilliseconds(100) - tick.Elapsed;
    if (wait > TimeSpan.Zero) await Task.Delay(wait);
}

foreach (var client in clients) await client.DisposeAsync();
bool ok = Interlocked.Read(ref inbox.Moved) > 0;
Console.WriteLine(ok ? "E2E OK" : "E2E FAILED: no position broadcasts received");
return ok ? 0 : 1;

static async Task ConnectWithRetry(ClusterClient client, string hostPort)
{
    int colon = hostPort.LastIndexOf(':');
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            await client.ConnectAsync(hostPort[..colon], int.Parse(hostPort[(colon + 1)..]));
            return;
        }
        catch when (attempt < 30)
        {
            await Task.Delay(1000); // servers may still be starting (docker compose / Aspire)
        }
    }
}

sealed class Inbox : IPlayerClient
{
    public long Moved;
    public long Chat;

    public void OnMoved(long playerId, float x, float y) => Interlocked.Increment(ref Moved);

    public void OnChat(long playerId, string text) => Interlocked.Increment(ref Chat);
}
