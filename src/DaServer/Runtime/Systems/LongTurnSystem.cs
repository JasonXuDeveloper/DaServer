namespace DaServer.Runtime;

/// <summary>
/// Turns cannot be cut short (their code still holds the actor), so a turn that never ends holds its actor until the
/// node stops. One that outlives the call timeout, whose caller has given up by then, is logged once, a few times a
/// second (<see cref="ShardIdleScan"/>).
/// </summary>
[UpdateInGroup<ShardIdleScan>]
internal sealed partial class LongTurnSystem : EcsSystem
{
    [ForEach]
    private static void Check(ref Turn turn, in ActorRef self, in ShardActivity activity, World world)
    {
        if (turn.Reported) return;
        long ms = activity.NowMs - turn.StartedMs;
        if (ms <= (long)world.Shard.Node.Options.CallTimeout.TotalMilliseconds) return;
        turn.Reported = true;
        Log.TurnTooLong(world.Logger, self.Actor, ms);
    }
}
