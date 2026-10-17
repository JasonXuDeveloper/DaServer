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
public sealed class Connection : FrameChannel, IFrameSender
{
    private int _failed; // flushed connections: a flush's write failed and the close is queued

    private readonly Socket _socket;
    private readonly ConnectionOptions _options;
    private readonly ISendTap? _tap;
    private readonly SendFlusher? _flusher;
    private int _writer; // flushed connections: 1 while the flusher or the carry loop writes to the socket
    private byte[] _carry = Array.Empty<byte>(); // flushed connections: what a flush could not write
    private int _carryOffset, _carryLength;

    /// <summary>The beat this connection is written on, if any (<see cref="ConnectionOptions.Flusher"/>).</summary>
    internal SendFlusher? Flusher => _flusher;

    public Connection(Socket socket, IChannelHandler handler, ConnectionOptions? options = null)
        : base(handler, QueueFor(options ??= new ConnectionOptions()))
    {
        _socket = socket;
        _options = options;
        _tap = options.Tap;
        _flusher = options.Flusher;
        socket.NoDelay = true;
        if (_flusher != null) socket.Blocking = false; // a flush's send returns WouldBlock instead of waiting
    }

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

    // A connection written on a beat keeps both buffers between beats; one written at once gives the written one back when idle.
    private static SendQueue QueueFor(ConnectionOptions options) =>
        new(options.MaxPendingSendBytes, options.SendBufferSize, options.ReleaseIdleBuffers, keepSpare: options.Flusher != null);

    void IFrameSender.Send(ReadOnlySpan<byte> frame) => Send(frame);

    private protected override void Wake()
    {
        if (_flusher != null) _flusher.Add(this); // written on the next beat
        else Queue.Wake();
    }

    private protected override void Abort()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // already disconnected
        }

        _socket.Dispose();
    }

    private async Task SendLoop()
    {
        try
        {
            while (true)
            {
                await Queue.Signal.WaitAsync().ConfigureAwait(false);
                while (!IsClosed && Queue.TryTake(out var batch, out int length))
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
                        Queue.Release(batch); // the spare while more is queued; idle: back to the pool
                    }
                }

                if (IsClosed)
                    return;
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            MarkClosed();
            Queue.Close(_tap); // queued but never written: the tap still sees it
        }
    }

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
            Handler.OnFrame(this, in header, new ReadOnlySpan<byte>(buffer, offset, header.Length));
            offset += header.Length;
        }

        needed = header.Length;
        return offset;
    }
}
