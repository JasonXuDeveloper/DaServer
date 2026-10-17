namespace DaServer.Runtime;

// The maintenance tick (every 100 ms): expired calls, links, gossip rounds, the stall watchdog, the features.
internal sealed partial class Node
{
    private SilenceClock _maintainClock;

    /// <summary>
    /// Tests: what a pause of this process of <paramref name="pause"/> looks like to its next maintenance tick
    /// (<paramref name="aged"/>: the members' progress clocks age by it too, as real time would have aged them).
    /// </summary>
    internal void SimulatePause(TimeSpan pause, bool aged = true)
    {
        _maintainClock.Rewind((long)pause.TotalMilliseconds);
        if (aged) Membership.Age((long)pause.TotalMilliseconds);
    }
    private long[]? _stallReported; // per shard: the pass already reported (the maintenance timer's thread only)
    private int _stalledShards;

    /// <summary>Shards whose current pass has run longer than <see cref="NodeOptions.StallAfter"/>.</summary>
    public int StalledShards => Volatile.Read(ref _stalledShards);

    /// <summary>The stall watchdog: a shard thread away from its loop longer than the threshold is reported once per pass.</summary>
    private void CheckStalls(long now)
    {
        var reported = _stallReported ??= new long[_shards.Length];
        long limit = (long)Options.StallAfter.TotalMilliseconds;
        int stalled = 0;
        for (int i = 0; i < _shards.Length; i++)
        {
            long ms = _shards[i].PassMs(now);
            if (ms < limit) continue;
            stalled++;
            long passStart = now - ms;
            if (reported[i] == passStart) continue;
            reported[i] = passStart;
            Log.ShardStalled(Logger, i, Options.Name, ms);
        }

        Volatile.Write(ref _stalledShards, stalled);
    }

    private void Maintain()
    {
        try
        {
            Pending.SweepExpired();
            long now = Platform.Milliseconds;
            bool late = Membership.Tick(now, _maintainClock.Tick(now)); // an excused pause: ping, no verdicts
            _slots.Tick(now);
            long interval = (long)Options.GossipInterval.TotalMilliseconds;
            long timeout = late ? long.MaxValue : Math.Max((long)Options.SuspectAfter.TotalMilliseconds, 3 * interval); // late: ping, no verdicts
            foreach (var link in Volatile.Read(ref _linkList))
            {
                link.AcknowledgeReceived();
                link.CheckAlive(now, interval, timeout);
            }

            CheckStalls(now);
            foreach (var feature in Options.Features) feature.Maintain(now, late);
        }
        catch (Exception e)
        {
            Log.MaintenanceFailed(Logger, e);
        }
    }
}
