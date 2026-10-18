namespace DaServer.Net;

// Connections written on a beat (ConnectionOptions.Flusher): the flusher writes without blocking, a carry loop finishes
// what the socket would not take.
internal sealed partial class Connection
{
    /// <summary>
    /// A beat of the flusher (its thread): write what is queued without blocking, unless the carry loop is writing (it
    /// drains the queue itself). What the socket does not take goes to the carry loop, which waits for room.
    /// </summary>
    internal void FlushNow()
    {
        if (IsClosed || Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return;
        while (Queue.TryTake(out var batch, out int length))
        {
            int offset = 0;
            while (offset < length)
            {
                int sent;
                SocketError error;
                try
                {
                    sent = _socket.Send(batch, offset, length - offset, SocketFlags.None, out error);
                }
                catch (ObjectDisposedException) // closed meanwhile
                {
                    Queue.Release(batch);
                    return;
                }

                if (error == SocketError.Success && sent > 0)
                {
                    offset += sent;
                    continue;
                }

                if (error is SocketError.WouldBlock or SocketError.Success) // full: the carry loop finishes it
                {
                    (_carry, _carryOffset, _carryLength) = (batch, offset, length);
                    Queue.Signal.Set();
                    return;
                }

                Queue.Release(batch);
                if (Interlocked.Exchange(ref _failed, 1) == 0) // never inline: the flusher holds no connection's locks
                    Platform.Queue(static c => c.Close(new SocketException((int)SocketError.ConnectionReset)), this);
                return;
            }

            KeepAlive.Touch(ref _lastSentMs);
            Queue.Release(batch);
        }

        Volatile.Write(ref _writer, 0);
    }

    /// <summary>
    /// The send loop of a flushed connection: idle until a flush leaves bytes the socket would not take, then it writes
    /// them as the socket drains, writes what queued meanwhile, and gives the writer back to the flusher.
    /// </summary>
    private async Task CarryLoop()
    {
        try
        {
            while (true)
            {
                await Queue.Signal.WaitAsync().ConfigureAwait(false);
                if (IsClosed) return;
                if (_carryLength == 0) continue;

                var (batch, offset, length) = (_carry, _carryOffset, _carryLength);
                (_carry, _carryOffset, _carryLength) = (Array.Empty<byte>(), 0, 0);
                while (true)
                {
                    while (offset < length)
                    {
                        int sent = await _socket.SendAsync(new ReadOnlyMemory<byte>(batch, offset, length - offset),
                            SocketFlags.None).ConfigureAwait(false);
                        if (sent <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                        offset += sent;
                    }

                    KeepAlive.Touch(ref _lastSentMs);
                    Queue.Release(batch);
                    if (!Queue.TryTake(out batch, out length)) break;
                    offset = 0;
                }

                Volatile.Write(ref _writer, 0);
                if (Queue.HasQueued) _flusher!.Add(this); // queued after the last take: next beat
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            MarkClosed();
            Queue.Close();
            BufferPool.Return(_carry);
        }
    }
}
