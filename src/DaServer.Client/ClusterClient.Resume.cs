namespace DaServer.Client;

// Lost connections and moves: reconnecting, resuming the session on a new connection, tickets.
public sealed partial class ClusterClient
{
    private void OnClosed(IFrameChannel connection, Exception? error)
    {
        if (connection != _connection)
        {
            // A connection the session moved away from (or a move's new one that failed): its calls get no reply now.
            _pending.FailOwnedBy(connection, RpcStatus.Disconnected);
            return;
        }

        Volatile.Write(ref _probeDeadlineMs, 0); // a probe of this connection must not close the next one
        if (Volatile.Read(ref _moving) != 0)
        {
            // The move takes over (it reconnects if it fails); the calls made on this connection get no reply now.
            _pending.FailOwnedBy(connection, RpcStatus.Disconnected);
            return;
        }
        Lost(error);
    }

    /// <summary>The current connection is gone: calls in flight fail, and the session reconnects if it can.</summary>
    private void Lost(Exception? error)
    {
        _pending.FailAll(RpcStatus.Disconnected);
        if (Volatile.Read(ref _reconnecting) != 0) return; // an attempt of the running reconnect failed: it retries
        Disconnected?.Invoke(error);
        if (!_disposed && AutoReconnect && Volatile.Read(ref _ticket) != null && Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0)
            _ = ReconnectAsync();
    }

    /// <summary>Connect again (through the endpoints: the cluster picks a live node) and resume the session.</summary>
    private async Task ReconnectAsync()
    {
        Exception? error = null;
        long deadline = Platform.Milliseconds + (long)ReconnectTimeout.TotalMilliseconds;
        var backoff = new Backoff(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        while (!_disposed)
        {
            var delay = backoff.Next();
            if (Platform.Milliseconds + (long)delay.TotalMilliseconds > deadline) break;
            await Task.Delay(delay).ConfigureAwait(false); // a blip or a restarting node: give it a moment
            try
            {
                IFrameChannel channel;
                // The attempt ends with the reconnect's deadline: a connect that hangs (a blackholed address) or a node
                // that never answers closes the attempt's connection, which fails its hello or resume.
                using (var attempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, deadline - Platform.Milliseconds))))
                using (attempt.Token.Register(static c => ((ClusterClient)c!)._connection?.Close(AttemptTimedOut), this))
                    (channel, _) = await ResumedAsync(watched: true, attempt.Token).ConfigureAwait(false);
                if (channel.IsClosed) throw AttemptTimedOut;
                if (_disposed)
                {
                    await GoodbyeAsync(channel).ConfigureAwait(false); // disposed meanwhile: the session ends at once
                    channel.Close();
                    break;
                }

                Use(channel);
                Volatile.Write(ref _reconnecting, 0);
                Reconnected?.Invoke();
                return;
            }
            catch (RpcException e) when (e.Status == RpcStatus.Forbidden)
            {
                error = e; // the ticket was refused: retrying cannot help
                break;
            }
            catch (Exception e)
            {
                error = e;
            }
        }

        Volatile.Write(ref _reconnecting, 0);
        if (!_disposed) ReconnectFailed?.Invoke(error);
    }

    private static readonly TimeoutException AttemptTimedOut = new("The reconnect attempt outlasted ReconnectTimeout");

    /// <summary>
    /// The node asked the client to move (<see cref="ControlOp.Retire"/>: its connection is old, or the node is
    /// leaving): the session resumes on a new connection through the entry addresses while the old one keeps working,
    /// so no push is lost and nothing waits. The old connection stays open until its node closes it (replies to calls
    /// made on it still arrive). If the move fails, the old connection's close later reconnects as usual.
    /// </summary>
    private async Task MoveAsync()
    {
        var old = _connection;
        if (old != null) Drain(old);
        bool leaving = false;
        try
        {
            var (channel, actors) = await ResumedAsync(watched: false).ConfigureAwait(false);
            if (_disposed)
            {
                await GoodbyeAsync(channel).ConfigureAwait(false); // disposed meanwhile: the session ends at once
                channel.Close();
                return;
            }

            Use(channel);
            if (old != null)
            {
                leaving = true;
                _ = LeaveAsync(old, actors);
            }

            Moved?.Invoke();
        }
        catch (Exception)
        {
            // stay on the old connection until it closes
        }
        finally
        {
            if (!leaving && old != null) Drained(old, keep: false); // no session took over: what its connection pushed is dropped
            Volatile.Write(ref _moving, 0);
            if (_connection is { IsClosed: true } && !_disposed) Lost(null); // it closed while the move ran and failed
        }
    }

    /// <summary>
    /// Leave a connection the session moved away from, as HTTP/2's GOAWAY drains one: the goodbye is its last frame
    /// (<see cref="IFrameChannel.SendLast"/>), so every frame sent on it before reaches the node first and every later
    /// send goes on the new connection; its node reports the old session closed at once instead of after the resume
    /// grace. The connection closes once the calls made on it are answered (replies come back the way calls went), or
    /// timed out. No step waits on a guess: the seal is the fence.
    /// </summary>
    private async Task LeaveAsync(IFrameChannel old, int actors)
    {
        long deadline = Platform.Milliseconds + (long)CallTimeout.TotalMilliseconds;
        // Every actor that took the new session marks the end of what it pushed to the old one (SessionMoved): pushes
        // still on their way there arrive before the new connection's go on. A closed connection brings no more.
        while (!old.IsClosed && Volatile.Read(ref _movedMarkers) < actors && Platform.Milliseconds < deadline)
            await Task.Delay(10).ConfigureAwait(false);
        try
        {
            await GoodbyeAsync(old).ConfigureAwait(false); // its answer is the last frame its session is sent
        }
        finally
        {
            Drained(old, keep: true);
        }

        while (!old.IsClosed && _pending.AnyOwnedBy(old) && Platform.Milliseconds < deadline)
            await Task.Delay(50).ConfigureAwait(false);
        old.Close();
    }

    /// <summary>Say goodbye as the last frame on <paramref name="channel"/> and wait for the node's answer (a second at most).</summary>
    private async Task GoodbyeAsync(IFrameChannel channel)
    {
        if (channel.IsClosed) return;
        var call = PendingCall<NoReply>.Rent(null);
        var goodbye = call.Done;
        Transmit(channel, FrameWriter.Rent(FrameHeader.Size), FrameKind.Control, 0, ControlOp.Goodbye, 0, call, RpcStatus.Ok, last: true);
        try
        {
            await Task.WhenAny(goodbye.AsTask(), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
        catch
        {
            // gone already, or slow: closing is all that is left
        }
    }

    /// <summary>
    /// A new introduced connection with the session resumed on it (closed again if the resume fails), and how many actors
    /// took the session.
    /// </summary>
    private async ValueTask<(IFrameChannel Channel, int Actors)> ResumedAsync(bool watched, CancellationToken cancellationToken = default)
    {
        var channel = await OpenAsync(watched, cancellationToken).ConfigureAwait(false);
        try
        {
            return (channel, await ResumeOn(channel).ConfigureAwait(false));
        }
        catch
        {
            channel.Close();
            throw;
        }
    }

    private ValueTask<int> ResumeOn(IFrameChannel channel)
    {
        var ticket = Volatile.Read(ref _ticket)!;
        var call = PendingCall<int>.Rent(null);
        var task = call.Task;
        var w = FrameWriter.Rent(FrameHeader.Size + ticket.Length);
        w.WriteRaw(ticket);
        Transmit(channel, w, FrameKind.Control, 0, ControlOp.ResumeSession, 0, call, RpcStatus.Ok);
        return task;
    }

    /// <summary>Ask for a fresh ticket at half of its lifetime (the sweeper timer, every 100 ms).</summary>
    private void RefreshTicket()
    {
        if (Volatile.Read(ref _ticket) == null || !IsConnected || Volatile.Read(ref _reconnecting) != 0) return;
        long now = Platform.Milliseconds;
        if (now < Volatile.Read(ref _refreshTicketAtMs)) return;
        Volatile.Write(ref _refreshTicketAtMs, now + 10_000); // until the new one arrives
        SendControl(FrameWriter.Rent(FrameHeader.Size), ControlOp.Ticket, null);
    }

    /// <summary>Tests: the latest resume ticket, and resuming with any ticket (a replay).</summary>
    internal byte[]? Ticket => Volatile.Read(ref _ticket);

    internal ValueTask<int> ResumeWith(byte[] ticket)
    {
        Volatile.Write(ref _ticket, ticket);
        return ResumeOn(_connection!);
    }

    /// <summary>Tests: lose the connection as a network failure would (no goodbye).</summary>
    internal void DropConnection() => _connection?.Close(new System.IO.IOException("Connection dropped"));
}
