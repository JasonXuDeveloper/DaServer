namespace DaServer.Net;

/// <summary>Anything frames are written to: a connection or a node link.</summary>
internal interface IFrameSender
{
    /// <summary>Send a complete frame (copied; the caller keeps ownership of the span).</summary>
    void Send(ReadOnlySpan<byte> frame);
}

/// <summary>
/// Sees every byte a connection hands to its socket, in order and before it is written (resumable node links keep
/// what the peer has not acknowledged). Called on the connection's send loop, outside its locks.
/// </summary>
internal interface ISendTap
{
    void OnSending(ReadOnlySpan<byte> bytes);
}

public sealed class ConnectionOptions
{
    /// <summary>Receive buffer size used while data is flowing.</summary>
    public int ReceiveBufferSize { get; set; } = 16 * 1024;

    /// <summary>
    /// Idle connections hold no buffer: a zero-byte read awaits readiness before a receive buffer is rented, and the
    /// send batch buffer goes back to the pool once everything queued is written (rented again by the next send).
    /// For client sessions and clients (many mostly idle sockets); off for busy node-to-node links.
    /// </summary>
    public bool ReleaseIdleBuffers { get; set; } = true;

    /// <summary>
    /// Max bytes queued and not yet handed to the socket. Exceeding it closes the connection (slow consumer),
    /// which bounds memory per connection under broadcast storms.
    /// </summary>
    public int MaxPendingSendBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Largest frame accepted from the peer (a larger length prefix closes the connection).</summary>
    public int MaxFrameSize { get; set; } = FrameHeader.MaxFrameSize;

    /// <summary>Initial pending-send batch buffer size.</summary>
    public int SendBufferSize { get; set; } = 16 * 1024;

    /// <summary>Receives a copy of the outgoing stream (node links, see <see cref="ISendTap"/>).</summary>
    internal ISendTap? Tap { get; set; }

    /// <summary>
    /// Writes this connection's queued bytes on its beat instead of at once (<see cref="SendFlusher"/>): frames queued
    /// within one interval leave in one write. Null: written as soon as they are queued (one write per batch).
    /// </summary>
    public SendFlusher? Flusher { get; set; }

    /// <summary>A node's side of client sessions: many mostly idle sockets, so idle ones hold no buffer.</summary>
    public static ConnectionOptions ForSessions() => new ConnectionOptions
    {
        ReceiveBufferSize = 4 * 1024, ReleaseIdleBuffers = true, MaxPendingSendBytes = 256 * 1024, SendBufferSize = 4 * 1024,
    };

    /// <summary>
    /// These options with connections writing on a beat of <paramref name="interval"/> (the process's shared flusher,
    /// <see cref="SendFlusher.Shared"/>), or at once when it is zero.
    /// </summary>
    public ConnectionOptions FlushedEvery(TimeSpan interval)
    {
        if (interval > TimeSpan.Zero) Flusher = SendFlusher.Shared(interval);
        return this;
    }

    /// <summary>
    /// A client's connection: one busy socket, so it keeps its buffers (a zero-byte read before every receive costs a
    /// system call per push, to save 4 KB).
    /// </summary>
    public static ConnectionOptions ForClients() => new ConnectionOptions
    {
        ReceiveBufferSize = 4 * 1024, ReleaseIdleBuffers = false, MaxPendingSendBytes = 256 * 1024, SendBufferSize = 4 * 1024,
    };

    public static ConnectionOptions ForNodeLinks() => new ConnectionOptions
    {
        ReceiveBufferSize = 256 * 1024, ReleaseIdleBuffers = false, MaxPendingSendBytes = 256 * 1024 * 1024,
        SendBufferSize = 256 * 1024,
    };
}

/// <summary>
/// Framed TCP connection.
/// <para>Send: frames are copied into a per-connection batch buffer under a short lock; one send loop swaps and
/// writes whole batches, so N small frames cost one syscall and broadcasting serializes once then memcpy's.</para>
/// <para>Receive: frames are parsed in place and handed to <see cref="IChannelHandler.OnFrame"/>.</para>
/// Steady state allocates nothing: buffers come from <see cref="BufferPool"/>, the two loops are allocated once.
/// </summary>
public sealed class Connection : IFrameSender, IFrameChannel
{
    private static readonly InvalidOperationException SlowConsumer =
        new("Send backlog exceeded MaxPendingSendBytes (slow consumer)");

    private int _failed; // flushed connections: a flush's write failed and the close is queued

    private readonly Socket _socket;
    private readonly IChannelHandler _handler;
    private readonly ConnectionOptions _options;
    private readonly SendQueue _queue;
    private int _closed;
    private readonly ISendTap? _tap;
    private readonly SendFlusher? _flusher;
    private int _writer; // flushed connections: 1 while the flusher or the carry loop writes to the socket
    private byte[] _carry = Array.Empty<byte>(); // flushed connections: what a flush could not write
    private int _carryOffset, _carryLength;
    private long _lastReceivedMs = Platform.Milliseconds;
    private long _lastSentMs = Platform.Milliseconds;

    public Connection(Socket socket, IChannelHandler handler, ConnectionOptions? options = null)
    {
        _socket = socket;
        _handler = handler;
        _options = options ?? new ConnectionOptions();
        _tap = _options.Tap;
        _flusher = _options.Flusher;
        // A connection written on a beat keeps both buffers between beats; one written at once gives the written one back when idle.
        _queue = new SendQueue(_options.MaxPendingSendBytes, _options.SendBufferSize, _options.ReleaseIdleBuffers, keepSpare: _flusher != null);
        socket.NoDelay = true;
        if (_flusher != null) socket.Blocking = false; // a flush's send returns WouldBlock instead of waiting
    }

    /// <summary>Free slot for the owner (e.g. session or peer node state).</summary>
    public object? Tag { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public long LastReceivedMs => Volatile.Read(ref _lastReceivedMs);

    public long LastSentMs => Volatile.Read(ref _lastSentMs);

    /// <summary>
    /// Connect to a host name or address. A name is connected through every address it resolves to ("localhost" is ::1
    /// first on many hosts while the peer may listen on IPv4 only, or the reverse), not only the first one.
    /// </summary>
    public static ValueTask<Connection> ConnectAsync(string host, int port, IChannelHandler handler,
        ConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        ConnectAsync(IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port),
            handler, options, cancellationToken);

    public static async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IChannelHandler handler,
        ConnectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // A DnsEndPoint (host name: containers, Aspire service discovery) has no family: use a dual-mode socket.
        var socket = endPoint.AddressFamily == AddressFamily.Unspecified
            ? new Socket(SocketType.Stream, ProtocolType.Tcp)
            : new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
#if NET
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
#else
            using (cancellationToken.Register(static s => ((Socket)s!).Dispose(), socket))
                await socket.ConnectAsync(endPoint).ConfigureAwait(false);
#endif
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var connection = new Connection(socket, handler, options);
        connection.Start();
        return connection;
    }

    /// <summary>Completes when the send loop has ended (every byte given to the connection reached the tap).</summary>
    internal Task SendCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the receive loop has ended (no frame of this connection is delivered any more).</summary>
    internal Task ReceiveCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Start the receive and send loops.</summary>
    public void Start()
    {
        ReceiveCompletion = ReceiveLoop();
        SendCompletion = _flusher != null ? CarryLoop() : SendLoop();
    }

    void IFrameSender.Send(ReadOnlySpan<byte> frame) => Send(frame);

    /// <summary>
    /// Queue a complete frame (header included). Thread-safe, never blocks on IO.
    /// Returns false when the connection is closed or was closed because the peer is too slow.
    /// </summary>
    public bool Send(ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref _closed) != 0)
            return false;

        switch (_queue.Append(frame))
        {
            case SendQueue.Result.Wake:
                if (_flusher != null) _flusher.Add(this); // written on the next beat
                else _queue.Wake(); // at once, or once when the thread's send batch ends
                return true;
            case SendQueue.Result.Queued:
                return true;
            case SendQueue.Result.Overflow:
                // Closed off the caller's thread: a fan-out holding a group's lock must not run the close callback inline.
                Platform.Queue(static c => c.Close(SlowConsumer), this);
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Until the returned scope is disposed, sends from this thread only queue bytes; each channel's writer is woken
    /// once at the end (<see cref="SendQueue.BeginBatch"/>).
    /// </summary>
    internal static SendQueue.Batch BeginSendBatch() => SendQueue.BeginBatch();

    public void Close(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // already disconnected
        }

        _socket.Dispose();
        _queue.Signal.Set();

        try
        {
            _handler.OnClosed(this, error);
        }
        catch
        {
            // handler errors must not escape the IO loops
        }
    }

    private async Task SendLoop()
    {
        try
        {
            while (true)
            {
                await _queue.Signal.WaitAsync().ConfigureAwait(false);
                while (Volatile.Read(ref _closed) == 0 && _queue.TryTake(out var batch, out int length))
                {
                    try
                    {
                        _tap?.OnSending(new ReadOnlySpan<byte>(batch, 0, length));
                        int offset = 0;
                        while (offset < length)
                        {
                            int sent = await _socket.SendAsync(new ReadOnlyMemory<byte>(batch, offset, length - offset),
                                SocketFlags.None).ConfigureAwait(false);
                            if (sent <= 0)
                                throw new SocketException((int)SocketError.ConnectionReset);
                            offset += sent;
                        }

                        KeepAlive.Touch(ref _lastSentMs);
                    }
                    finally
                    {
                        _queue.Release(batch); // the spare while more is queued; idle: back to the pool
                    }
                }

                if (Volatile.Read(ref _closed) != 0)
                    return;
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            _queue.Close(_tap); // queued but never written: the tap still sees it
        }
    }

    /// <summary>
    /// A beat of the flusher (its thread): write what is queued without blocking, unless the carry loop is writing (it
    /// drains the queue itself). What the socket does not take goes to the carry loop, which waits for room.
    /// </summary>
    internal void FlushNow()
    {
        if (Volatile.Read(ref _closed) != 0 || Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return;
        while (_queue.TryTake(out var batch, out int length))
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
                    _queue.Release(batch);
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
                    _queue.Signal.Set();
                    return;
                }

                _queue.Release(batch);
                if (Interlocked.Exchange(ref _failed, 1) == 0) // never inline: the flusher holds no connection's locks
                    Platform.Queue(static c => c.Close(new SocketException((int)SocketError.ConnectionReset)), this);
                return;
            }

            KeepAlive.Touch(ref _lastSentMs);
            _queue.Release(batch);
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
                await _queue.Signal.WaitAsync().ConfigureAwait(false);
                if (Volatile.Read(ref _closed) != 0) return;
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
                    _queue.Release(batch);
                    if (!_queue.TryTake(out batch, out length)) break;
                    offset = 0;
                }

                Volatile.Write(ref _writer, 0);
                if (_queue.HasQueued) _flusher!.Add(this); // queued after the last take: next beat
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            _queue.Close();
            BufferPool.Return(_carry);
        }
    }

    private async Task ReceiveLoop()
    {
        byte[] buffer = Array.Empty<byte>();
        int filled = 0;
        Exception? error = null;
        try
        {
            while (true)
            {
                if (filled == 0 && _options.ReleaseIdleBuffers)
                {
                    // Wait for readiness without holding a buffer.
                    if (buffer.Length > 0)
                    {
                        BufferPool.Return(buffer);
                        buffer = Array.Empty<byte>();
                    }

                    await _socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.None).ConfigureAwait(false);
                }

                if (buffer.Length == 0)
                    buffer = BufferPool.Rent(_options.ReceiveBufferSize);

                int read = await _socket.ReceiveAsync(new Memory<byte>(buffer, filled, buffer.Length - filled),
                    SocketFlags.None).ConfigureAwait(false);
                if (read == 0)
                    break; // graceful close
                KeepAlive.Touch(ref _lastReceivedMs);

                filled += read;

                int consumed;
                int needed;
                using (BeginSendBatch()) consumed = ParseFrames(buffer, filled, out needed);
                if (consumed > 0)
                {
                    filled -= consumed;
                    if (filled > 0)
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, filled);
                }

                if (needed > buffer.Length)
                {
                    // A frame larger than the current buffer: grow to fit it.
                    BufferPool.Grow(ref buffer, filled, needed);
                }
                else if (filled == 0 && buffer.Length > _options.ReceiveBufferSize * 4)
                {
                    // Shrink back after a burst of large frames.
                    BufferPool.Return(buffer);
                    buffer = Array.Empty<byte>();
                }
            }
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            BufferPool.Return(buffer);
        }

        Close(error);
    }

    /// <summary>Dispatch every complete frame in buffer[0..filled]; returns bytes consumed.</summary>
    private int ParseFrames(byte[] buffer, int filled, out int needed)
    {
        int offset = 0;
        FrameHeader header;
        while (FrameHeader.TryReadFrame(new ReadOnlySpan<byte>(buffer, offset, filled - offset), _options.MaxFrameSize, out header))
        {
            _handler.OnFrame(this, in header, new ReadOnlySpan<byte>(buffer, offset, header.Length));
            offset += header.Length;
        }

        needed = header.Length;
        return offset;
    }
}
