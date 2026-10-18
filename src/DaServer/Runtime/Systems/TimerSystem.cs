namespace DaServer.Runtime;

/// <summary>A message to send later (<see cref="ICluster.SendAfter{TAddr, T}"/>): serialized when scheduled, routed when due.</summary>
internal readonly record struct DelayedSend(FrameWriter Frame, AddressKind Kind, uint Scope, uint MessageId, ulong Node, long Value);

/// <summary>
/// The shard's delayed sends: a timer wheel owned by the shard thread (scheduled and cancelled by handlers and
/// systems of this shard). A handle carries the shard's index in its top byte, so another shard's handle is refused.
/// </summary>
internal readonly record struct ShardTimers(TimerWheel<DelayedSend> Wheel) : ISingletonComponent
{
    /// <summary>Wheel granularity: a delayed send fires at most about two of these late (the shard sleeps no longer while one waits).</summary>
    public static readonly TimeSpan Resolution = TimeSpan.FromMilliseconds(5);

    public long Schedule(int shard, TimeSpan delay, in DelayedSend send) => (long)shard << 56 | Wheel.Schedule(delay, in send);

    /// <summary>The shard stops: what was not sent yet is dropped (one-way delivery is at most once).</summary>
    public void Drop()
    {
        var drop = new Dropped();
        Wheel.Clear(ref drop);
    }

    private readonly struct Dropped : ITimerHandler<DelayedSend>
    {
        public void OnTimer(in DelayedSend send) => send.Frame.Dispose();
    }

    public bool Cancel(int shard, long handle)
    {
        if ((int)((ulong)handle >> 56) != shard) return false;
        if (!Wheel.Cancel(handle & 0x00FF_FFFF_FFFF_FFFF, out var send)) return false;
        send.Frame.Dispose();
        return true;
    }
}

/// <summary>Sends the delayed messages that are due, as if sent now: they follow moved actors and reach new members.</summary>
[UpdateInGroup<ShardMessages>]
internal sealed class TimerSystem : EcsSystem
{
    public override int Priority => -1; // before the mail: what is due joins this wake's sends

    protected override void OnUpdate()
    {
        ref var timers = ref World.GetSingleton<ShardTimers>();
        if (timers.Wheel.Count == 0) return;
        var due = new Due(World.Shard.Node);
        timers.Wheel.Advance(ref due);
    }

    private readonly struct Due(Node node) : ITimerHandler<DelayedSend>
    {
        public void OnTimer(in DelayedSend send) => node.Deliver(send.Frame, send.Kind, send.Scope, send.MessageId, send.Node, send.Value);
    }
}
