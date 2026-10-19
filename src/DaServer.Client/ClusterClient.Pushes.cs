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
                Interlocked.Increment(ref _movedMarkers); // the runtime's marker, not a push for the handlers
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
            Volatile.Write(ref _movedMarkers, 0);
        }
    }

    private int _movedMarkers; // SessionMoved received on the connection a move drains

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
