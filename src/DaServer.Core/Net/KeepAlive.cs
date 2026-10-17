namespace DaServer.Net;

/// <summary>
/// Liveness of frame channels (ADR-053). A peer is alive while frames arrive: any frame counts, so busy connections
/// carry no extra traffic; a side that has sent or received nothing for an interval sends a header-only
/// <see cref="ControlOp.Ping"/>, answered with <see cref="ControlOp.Pong"/>, so a connection still hears from its peer
/// when nothing else comes back (an idle one, or one that only sends) and NAT and load balancer mappings stay open. A connection that has received nothing for its timeout is closed: half-open TCP
/// (a vanished host, a dropped NAT mapping, a switched mobile network) and a peer that stopped reading are noticed in
/// seconds, not after TCP's retransmission timeout. Closing is never a verdict on the peer: the owner reconnects and
/// resumes (client tickets, node link resend windows).
/// </summary>
public static class KeepAlive
{
    /// <summary>Why a connection closed for silence (shared: closing allocates nothing).</summary>
    public static readonly TimeoutException Silent = new("Nothing received within the keep-alive timeout");

    /// <summary>
    /// Close <paramref name="channel"/> when it has received nothing for <paramref name="timeoutMs"/>; else send a
    /// ping when it has sent or received nothing for <paramref name="intervalMs"/> (0: never ping; while the peer
    /// stays silent, a ping per check until it answers). Times are
    /// <see cref="Platform.Milliseconds"/>. False when the channel is (now) closed.
    /// </summary>
    public static bool Check(IFrameChannel channel, long nowMs, long intervalMs, long timeoutMs)
    {
        if (channel.IsClosed) return false;
        if (nowMs - channel.LastReceivedMs >= timeoutMs)
        {
            channel.Close(Silent);
            return false;
        }

        if (intervalMs > 0 && nowMs - Math.Min(channel.LastSentMs, channel.LastReceivedMs) >= intervalMs) channel.SendControl(ControlOp.Ping);
        return true;
    }

    /// <summary>Answer a ping (any side; on the connection it arrived on).</summary>
    public static void Pong(IFrameChannel channel) => channel.SendControl(ControlOp.Pong);
}

/// <summary>
/// The clock of a periodic liveness check: a pass that runs more than a second after the previous one ran late (a GC
/// pause, a starved machine), which says nothing about the peers, so it passes no silence verdicts.
/// </summary>
internal struct SilenceClock
{
    private long _lastMs;

    /// <summary>Record a pass at <paramref name="nowMs"/>; true when it ran late.</summary>
    public bool Tick(long nowMs)
    {
        long last = Interlocked.Exchange(ref _lastMs, nowMs);
        return last != 0 && nowMs - last > 1000;
    }
}
