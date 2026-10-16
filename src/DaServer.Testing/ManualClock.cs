namespace DaServer.Testing;

/// <summary>
/// A clock that moves only when the test moves it (<see cref="TestCluster.TickAsync"/>), starting at a fixed instant so
/// runs repeat. Timers on it are refused: shard code schedules with <c>SendAfter</c> (DAS015), which follows this clock.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _elapsed = TimeSpan.TicksPerDay; // timestamps far from 0, as a real monotonic clock's

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _elapsed);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(GetTimestamp() - TimeSpan.TicksPerDay);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        throw new NotSupportedException("A manual clock has no timers: delay messages with SendAfter, which follows it");

    public void Advance(long timestamps) => Interlocked.Add(ref _elapsed, timestamps);
}
