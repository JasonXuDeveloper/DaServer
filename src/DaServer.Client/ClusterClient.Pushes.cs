namespace DaServer.Client;

// Pushes: delivered to the registered handlers in order, one connection at a time; a move parks the new connection's
// until the old one drained (each actor's SessionMoved, then the goodbye); handler failures are reported.
public sealed partial class ClusterClient
{
    /// <summary>
    /// A pushed frame: to the callbacks (inline, or queued for <see cref="ProcessCallbacks"/>), one connection at a time.
    /// While a move drains the connection left, pushes of the new one wait: an actor pushes to the old session, then
    /// takes the new one, so what the old connection carries was sent first.
    /// </summary>
    private void Deliver(IFrameChannel connection, ReadOnlySpan<byte> frame)
    {
        // Outside a move one connection delivers (one receive loop): no lock. A move sets _draining before its new
        // connection exists and clears it only after the parked pushes went on, both under the gate.
        if (Volatile.Read(ref _draining) == null)
        {
            Hand(connection, frame);
            return;
        }

        lock (_pushGate)
        {
            if (_draining != null && connection != _draining)
            {
                if (_parked.Count < MaxParked) _parked.Enqueue(FrameWriter.CopyOf(frame));
                else connection.Close(CallbacksBehind);
                return;
            }

            if (connection == _draining && FrameHeader.Read(frame).MessageId == TypeId<SessionMoved>.Value)
            {
                if (++_movedMarkers >= _markersAwaited) _allMoved?.TrySetResult(true); // the runtime's marker, not a push
                return;
            }

            Hand(connection, frame);
        }
    }

    private void Hand(IFrameChannel connection, ReadOnlySpan<byte> frame)
    {
        if (_inbox == null) DispatchCallback(frame);
        else Enqueue(connection, frame);
    }

    /// <summary>A move starts: pushes of other connections wait until <paramref name="old"/> drained.</summary>
    private void Drain(IFrameChannel old)
    {
        lock (_pushGate)
        {
            Volatile.Write(ref _draining, old);
            _movedMarkers = 0;
            _markersAwaited = int.MaxValue; // until the resume says how many actors took the session
            _allMoved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // Under the gate: SessionMoved received on the connection a move drains, how many it waits for, and the signal.
    private int _movedMarkers, _markersAwaited;
    private TaskCompletionSource<bool>? _allMoved;

    /// <summary>
    /// Completes once each of the <paramref name="actors"/> that took the session sent its <see cref="SessionMoved"/> on
    /// <paramref name="old"/>, or <paramref name="old"/> closed (it brings no more).
    /// </summary>
    private Task MarkersOf(IFrameChannel old, int actors)
    {
        lock (_pushGate)
        {
            if (_draining != old || _allMoved == null) return Task.CompletedTask;
            _markersAwaited = actors;
            if (_movedMarkers >= actors || old.IsClosed) _allMoved.TrySetResult(true);
            return _allMoved.Task;
        }
    }

    /// <summary>A connection other than the current one closed: if a move drains it, no marker comes any more.</summary>
    private void DrainedConnectionClosed(IFrameChannel connection)
    {
        lock (_pushGate)
            if (_draining == connection) _allMoved?.TrySetResult(true);
    }

    /// <summary>
    /// The connection <paramref name="old"/> left drained (its goodbye was answered, or it closed), or the move failed (<paramref name="keep"/>
    /// false: the pushes of a session that did not take over are dropped): the waiting pushes go on, in order.
    /// </summary>
    private void Drained(IFrameChannel old, bool keep)
    {
        lock (_pushGate)
        {
            if (_draining != old) return; // a later move drains its own connection now
            while (_parked.TryDequeue(out var frame))
            {
                try
                {
                    if (keep && _connection is { } current) Hand(current, frame.Frame);
                }
                finally
                {
                    frame.Dispose();
                }
            }

            Volatile.Write(ref _draining, null); // after the parked ones: a push arriving meanwhile waited on the gate
        }
    }

    /// <summary>Queue a pushed frame for <see cref="ProcessCallbacks"/>; a full queue disconnects (slow consumer).</summary>
    private void Enqueue(IFrameChannel connection, ReadOnlySpan<byte> frame)
    {
        var copy = FrameWriter.CopyOf(frame);
        if (_inbox!.TryEnqueue(copy)) return;
        copy.Dispose();
        connection.Close(CallbacksBehind);
    }

    private static readonly InvalidOperationException CallbacksBehind =
        new("Pushes waiting for ProcessCallbacks exceeded the client's callbackQueueCapacity (slow consumer)");

    private void DispatchCallback(ReadOnlySpan<byte> frame)
    {
        var header = FrameHeader.Read(frame);
        var registered = Volatile.Read(ref _handlers);
        for (int i = 0; i < registered.Length; i++)
        {
            if (registered[i].Handlers.Of(header.MessageId) is not { } handler) continue;
            var context = default(CallContext);
            try
            {
                var task = handler.Dispatcher(registered[i].Target, FrameHeader.PayloadOf(in header, frame), in context);
                if (task.IsCompleted) task.GetAwaiter().GetResult();
                else _ = Observe(task, this);
            }
            catch (Exception e)
            {
                Failed(e); // the next handler and the connection go on
            }
        }
    }

    private static async Task Observe(ValueTask task, ClusterClient client)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            client.Failed(e);
        }
    }

    /// <summary>Raise one of the client's events: a subscriber that throws is reported (<see cref="HandlerFailed"/>) and the client goes on.</summary>
    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    /// <inheritdoc cref="Raise(Action?)"/>
    private void Raise(Action<Exception?>? handler, Exception? error)
    {
        try
        {
            handler?.Invoke(error);
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private void Failed(Exception error)
    {
        try
        {
            HandlerFailed?.Invoke(error);
        }
        catch (Exception)
        {
            // A failing subscriber must not take the connection down either.
        }
    }
}
