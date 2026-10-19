namespace DaServer.Tests;

/// <summary>Timer wheels.</summary>
public partial class InfrastructureTests
{
    [Test]
    public void AHandleCancelsOnlyOnTheWheelThatMadeIt()
    {
        // An actor that moved may keep a handle from its previous node: on its new node's wheel it must not cancel the
        // timer of the same entry there.
        var first = new TimerWheel<int>(TimeSpan.FromMilliseconds(5));
        var second = new TimerWheel<int>(TimeSpan.FromMilliseconds(5));
        long stale = first.Schedule(TimeSpan.FromSeconds(1), 1);
        long own = second.Schedule(TimeSpan.FromSeconds(1), 2);
        Assert.That(second.Cancel(stale), Is.False, "another wheel's handle");
        Assert.That(second.Cancel(own), Is.True);
    }

    [Test]
    public void ATimerScheduledAfterALongIdleTimeFiresOnTimeWithoutWalkingTheIdleTicks()
    {
        var clock = new DaServer.Testing.ManualClock();
        var wheel = new TimerWheel<int>(TimeSpan.FromMilliseconds(5), time: clock);
        var fired = new Fired();
        wheel.Schedule(TimeSpan.Zero, 1);
        clock.Advance(TimeSpan.FromMilliseconds(10).Ticks);
        wheel.Advance(ref fired); // warm, and empty again

        clock.Advance(TimeSpan.FromDays(365).Ticks); // a quiet year: about 6.3 billion ticks of 5 ms, minutes to walk
        var watch = System.Diagnostics.Stopwatch.StartNew();
        wheel.Schedule(TimeSpan.FromMilliseconds(10), 2);
        clock.Advance(TimeSpan.FromMilliseconds(5).Ticks);
        wheel.Advance(ref fired);
        int early = fired.Items.Count;
        clock.Advance(TimeSpan.FromMilliseconds(10).Ticks);
        wheel.Advance(ref fired);
        watch.Stop();
        Assert.That(early, Is.EqualTo(1), "nothing fires early");
        Assert.That(fired.Items, Is.EqualTo(new[] { 1, 2 }), "it fires on time");
        Assert.That(watch.ElapsedMilliseconds, Is.LessThan(2000), "the idle year was skipped, not walked tick by tick");
    }

    [Test]
    public void TimerWheelsFireDueTimersOnceAndCancelOthers()
    {
        var wheel = new TimerWheel<int>(TimeSpan.FromMilliseconds(5), slots: 8); // 40 ms a round: 100 ms is a later round
        var fired = new Fired();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        wheel.Schedule(TimeSpan.FromMilliseconds(30), 3);
        wheel.Schedule(TimeSpan.FromMilliseconds(10), 1);
        long cancelled = wheel.Schedule(TimeSpan.FromMilliseconds(20), 2);
        wheel.Schedule(TimeSpan.FromMilliseconds(100), 4);
        Assert.That(wheel.Cancel(cancelled), Is.True);
        Assert.That(wheel.Cancel(cancelled), Is.False, "once");

        while (wheel.Count > 0 && watch.ElapsedMilliseconds < 2000) wheel.Advance(ref fired);
        Assert.That(fired.Items, Is.EqualTo(new[] { 1, 3, 4 }));
        Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(100), "nothing fires early");

        // Steady state: schedule and fire without allocating once the entry table reached its peak (the first round).
        var counting = new Counting();
        long before = 0;
        for (int round = 0; round < 2; round++)
        {
            if (round == 1) before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) wheel.Schedule(TimeSpan.Zero, i);
            while (wheel.Count > 0) wheel.Advance(ref counting);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(counting.Count, Is.EqualTo(2000));
    }

    private struct Fired() : ITimerHandler<int>
    {
        public readonly List<int> Items = [];

        public void OnTimer(in int item) => Items.Add(item);
    }

    private struct Counting : ITimerHandler<int>
    {
        public int Count;

        public void OnTimer(in int item) => Count++;
    }
}
