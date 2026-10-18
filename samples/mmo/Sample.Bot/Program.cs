using ConsoleAppFramework;
using DaServer.Client;
using DaServer.Simulation;
using DaServer.Simulation.Planning;
using Sample.Bot;
using Sample.Client;
using Sample.Contracts;

// The sample's load test and end-to-end test: a swarm of simulated players (DaServer.Simulation) runs the game client
// (Sample.Client) with a planner at the controls (Behavior). The entry addresses come from Aspire's WithClientEndpoints.
await ConsoleApp.RunAsync(args, Scenario.RunAsync);

internal static class Scenario
{
    /// <summary>
    /// Bots log in, enter a map, then wander, chat and change maps while the realm broadcasts its status to every
    /// session. Exit code 0 only if, during the measured window, every bot heard every realm broadcast in order, none
    /// disconnected, every map change succeeded and the broadcasts' p99 latency stayed under the limit.
    /// </summary>
    /// <param name="bots">Simulated players.</param>
    /// <param name="seconds">Length of the measured window.</param>
    /// <param name="maps">Maps the bots start in (0: one per 100 bots).</param>
    /// <param name="turnsPerSecond">How often a bot changes direction.</param>
    /// <param name="chatEvery">Seconds between a bot's chat lines (0: never).</param>
    /// <param name="travelEvery">Seconds between a bot's map changes (0: never).</param>
    /// <param name="maxP99">Realm broadcast p99 latency limit, ms.</param>
    /// <param name="lean">Bots keep no view of other avatars: the servers' and the network's load is the same, the
    /// clients' presentation state is skipped (load tests with the servers on the same machine).</param>
    public static async Task<int> RunAsync(int bots = 1000, int seconds = 30, int maps = 0, int turnsPerSecond = 1,
        int chatEvery = 10, int travelEvery = 0, int maxP99 = 250, bool lean = false)
    {
        if (maps <= 0) maps = Math.Max(1, bots / 100);
        var endpoints = Endpoints.FromEnvironment();
        await using var swarm = new Swarm(new SwarmOptions
        {
            Loops = Math.Min(Environment.ProcessorCount, bots),
            TickRate = Timing.TickRate,
            OnPass = ClientSystems.OnPass,
            Configure = static (world, meter) =>
            {
                ClientSystems.AddTo(world);
                Behavior.AddTo(world, meter);
            },
        });
        var meter = swarm.Meter;
        var (positions, lines, gaps) = (meter.Counter("positions (sampled)", SwarmCounterKind.Rate),
            meter.Counter("chat", SwarmCounterKind.Rate), meter.Counter("gaps", SwarmCounterKind.Failure));
        var telemetry = new Telemetry[bots];

        await swarm.StartAsync(bots, static () => new ClusterClient(callbackQueueCapacity: 256), async user =>
        {
            int i = user.Index;
            await user.ConnectAsync(endpoints);
            user.Client.Register(telemetry[i] = new Telemetry(meter, lines, gaps));
            if (i % PositionTelemetry.Every == 0) user.Client.Register(new PositionTelemetry(positions));
            var player = Player.Create(user.World, user.Client, view: !lean);
            if (!await Swarm.Retry(() => Player.LogInAsync(player, "bot-" + i))) throw new InvalidOperationException("login refused: bot-" + i);
            await Swarm.Retry(() => Player.EnterMapAsync(player, new MapId(1 + i % maps)));
            // Schedules start spread over a second, so input does not come in waves. The random seed spreads the bot's index
            // (Knuth's multiplicative hash) and is odd: xorshift never starts from 0.
            player.Add<Bot>() = new Bot((uint)i * 2654435761u | 1, turnsPerSecond > 0 ? 1000 / turnsPerSecond : 0,
                chatEvery * 1000, travelEvery * 1000, maps, Environment.TickCount64 + i % 1000);
            player.Add<Facts>();
            player.Add<Plan>() = new Plan();
        });

        await swarm.WaitAsync(() => Task.FromResult((long)telemetry.Count(static t => !t.Heard)), TimeSpan.FromSeconds(30));
        var result = await swarm.MeasureAsync(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(5), maxP99);
        long silent = telemetry.Count(static t => t.Measured == 0);
        return result
            .Expect(silent == 0, $"{silent} bots heard no realm broadcast while measured")
            .Expect(turnsPerSecond == 0 || positions.Value > 0, "no position reached any bot")
            .Report();
    }
}
