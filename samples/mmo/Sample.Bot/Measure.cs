using DaServer.Simulation;
using EasyEcs;
using Sample.Client;
using Sample.Contracts;

namespace Sample.Bot;

/// <summary>The scenario's own counters, as a singleton of every loop's world.</summary>
public readonly record struct BotMeters(Counter Gaps, Counter FailedTrips) : ISingletonComponent
{
    public static BotMeters Of(Meter meter) => new(meter.Counter("gaps"), meter.Counter("failed map changes"));
}

/// <summary>What the measurement last saw of a player's realm status, and how many arrived while measuring.</summary>
public record struct Measured(long Tick, long Received, long InWindow) : IComponent;

/// <summary>
/// After the client handled its pushes, measures each realm status it received: the latency from the game node's
/// system to the client's handler, and gaps (a missed or reordered broadcast).
/// </summary>
[UpdateInGroup<ClientPushes>]
public sealed partial class MeasureSystem : EcsSystem
{
    public override int Priority => 10; // after PushSystem

    private const double TickMs = 1000.0 / Timing.TickRate;

    [ForEach]
    private static void Measure(in Realm realm, ref Measured seen, in Measurements measurements, in BotMeters meters)
    {
        long arrived = realm.Received - seen.Received;
        if (arrived == 0) return;
        var meter = measurements.Meter;
        bool measuring = meter.Measuring;
        if (measuring)
        {
            if (seen.Tick != 0 && realm.Tick != seen.Tick + arrived * Timing.RealmStatusEveryTicks)
            {
                if (meters.Gaps.Value < 5) Console.WriteLine($"gap: realm tick {seen.Tick} then {realm.Tick}");
                meters.Gaps.Add();
            }
            // Statuses handled in the same pass: the earlier ones were sent a broadcast interval apart before the last.
            double latest = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - realm.ServerTimeTicks).TotalMilliseconds;
            for (long i = 0; i < arrived; i++) meter.Latency.Add(latest + i * Timing.RealmStatusEveryTicks * TickMs);
        }

        seen = new Measured(realm.Tick, realm.Received, seen.InWindow + (measuring ? arrived : 0));
    }
}
