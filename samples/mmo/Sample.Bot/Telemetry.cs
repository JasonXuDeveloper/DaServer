using DaServer;
using DaServer.Simulation;
using Sample.Contracts;

namespace Sample.Bot;

/// <summary>
/// What one bot measures, as a second subscriber of its client's pushes (the client's own handler writes the game
/// state): realm broadcast latency from the game node's system to here, gaps in their sequence, chat lines received.
/// Counting happens on the bot's loop; the meter's counters are striped.
/// </summary>
internal sealed class Telemetry(SwarmMeter meter, SwarmCounter lines, SwarmCounter gaps)
{
    private long _lastTick, _measured;

    [Handle]
    public void On(RealmStatus status)
    {
        long previous = _lastTick;
        _lastTick = status.Tick;
        if (!meter.Measuring) return;
        if (previous != 0 && status.Tick != previous + Timing.RealmStatusEveryTicks)
        {
            if (gaps.Value < 5) Console.WriteLine($"gap: realm tick {previous} then {status.Tick}");
            gaps.Add();
        }

        meter.Latency.Add(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - status.ServerTimeTicks).TotalMilliseconds);
        Volatile.Write(ref _measured, _measured + 1);
    }

    [Handle] public void On(Chat _) => lines.Add();

    /// <summary>Whether this bot heard the realm at all, and how often while measured.</summary>
    public bool Heard => Volatile.Read(ref _lastTick) != 0;

    public long Measured => Volatile.Read(ref _measured);
}

/// <summary>
/// A sampled bot also counts the positions it receives (every <see cref="Every"/>th bot, reported times
/// <see cref="Every"/>): a subscriber deserializes each push again, and positions are the bulk of the traffic.
/// </summary>
internal sealed class PositionTelemetry(SwarmCounter positions)
{
    public const int Every = 16;

    [Handle]
    public void On(Motion m)
    {
        positions.Add(m.Avatars.Items.Count * Every);
        m.Avatars.ReturnToPool();
    }
}
