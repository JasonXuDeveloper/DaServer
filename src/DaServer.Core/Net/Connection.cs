namespace DaServer.Net;

/// <summary>Receives frames and lifecycle events of a <see cref="Connection"/>.</summary>
public interface IConnectionHandler
{
    /// <summary>
    /// Called on the connection's receive loop for every complete frame. <paramref name="frame"/> (header included)
    /// is only valid during the call: copy it if it must outlive the call. Must not block.
    /// </summary>
    void OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame);

    /// <summary>Called exactly once when the connection closes.</summary>
    void OnClosed(Connection connection, Exception? error);
}

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

    public static ConnectionOptions ForClients() => new ConnectionOptions
    {
        ReceiveBufferSize = 4 * 1024, ReleaseIdleBuffers = true, MaxPendingSendBytes = 256 * 1024, SendBufferSize = 4 * 1024,
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
/// <para>Receive: frames are parsed in place and handed to <see cref="IConnectionHandler.OnFrame"/>.</para>
/// Steady state allocates nothing: buffers come from <see cref="BufferPool"/>, the two loops are allocated once.
/// </summary>
public sealed class Connection : IFrameSender, IFrameChannel
{
    private static readonly InvalidOperationException SlowConsumer =
        new("Send backlog exceeded MaxPendingSendBytes (slow consumer)");

    private int _overflowed;

    private readonly Socket _socket;
    private readonly IConnectionHandler _handler;
    private readonly ConnectionOptions _options;
    private readonly AsyncSignal _sendSignal = new AsyncSignal();
    private readonly Lock _sendLock = new Lock();
    private byte[] _pending;
    private int _pendingLength;
    private int _closed;
    private readonly ISendTap? _tap;

    public Connection(Socket socket, IConnectionHandler handler, ConnectionOptions? options = null)
    {
        _socket = socket;
        _handler = handler;
        _options = options ?? new ConnectionOptions();
        _pending = Array.Empty<byte>();
        _tap = _options.Tap;
        socket.NoDelay = true;
    }

    /// <summary>Free slot for the owner (e.g. session or peer node state).</summary>
    public object? Tag { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Connect to a host name or address. A name is connected through every address it resolves to ("localhost" is ::1
    /// first on many hosts while the peer may listen on IPv4 only, or the reverse), not only the first one.
    /// </summary>
    public static ValueTask<Connection> ConnectAsync(string host, int port, IConnectionHandler handler,
        ConnectionOptions? options = null, CancellationToken cancellationToken = default) =>
        ConnectAsync(IPAddress.TryParse(host, out var ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port),
            handler, options, cancellationToken);

    public static async ValueTask<Connection> ConnectAsync(EndPoint endPoint, IConnectionHandler handler,
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
        SendCompletion = SendLoop();
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

        bool overflow = false;
        bool wake;
        lock (_sendLock)
        {
            if (Volatile.Read(ref _closed) != 0)
                return false;

            int required = _pendingLength + frame.Length;
            if (required > _options.MaxPendingSendBytes)
            {
                overflow = true;
                wake = false;
            }
            else
            {
                if (required > _pending.Length)
                    GrowPending(required);

                frame.CopyTo(new Span<byte>(_pending, _pendingLength, frame.Length));
                wake = _pendingLength == 0;
                _pendingLength = required;
            }
        }

        if (overflow)
        {
            // Closed off the caller's thread: a fan-out holding a group's lock must not run the close callback inline.
            if (Interlocked.Exchange(ref _overflowed, 1) == 0)
                Platform.Queue(static c => c.Close(SlowConsumer), this);
            return false;
        }

        if (wake)
        {
            if (t_batchDepth > 0) (t_batched ??= new()).Add(this); // woken once when the batch ends
            else _sendSignal.Set();
        }

        return true;
    }

    [ThreadStatic] private static int t_batchDepth;
    [ThreadStatic] private static List<Connection>? t_batched;

    /// <summary>
    /// Until the returned scope is disposed, sends from this thread only queue bytes; each connection's send loop is
    /// woken once at the end. A batch of work that sends many messages (the frames of one receive, one shard wake) then
    /// costs one write per connection instead of one per message (a session in 9 groups gets 9 frames per update).
    /// </summary>
    internal static SendBatch BeginSendBatch()
    {
        t_batchDepth++;
        return default;
    }

    internal readonly struct SendBatch : IDisposable
    {
        public void Dispose()
        {
            if (--t_batchDepth > 0 || t_batched is not { Count: > 0 } batched) return;
            foreach (var connection in batched) connection._sendSignal.Set();
            batched.Clear();
        }
    }

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
        _sendSignal.Set();

        try
        {
            _handler.OnClosed(this, error);
        }
        catch
        {
            // handler errors must not escape the IO loops
        }
    }

    private void GrowPending(int required)
    {
        int size = Math.Max(_options.SendBufferSize, Math.Max(_pending.Length * 2, required));
        var next = BufferPool.Rent(size);
        if (_pendingLength > 0)
            Buffer.BlockCopy(_pending, 0, next, 0, _pendingLength);
        BufferPool.Return(_pending);
        _pending = next;
    }

    private async Task SendLoop()
    {
        byte[] sending = Array.Empty<byte>();
        try
        {
            while (true)
            {
                await _sendSignal.WaitAsync().ConfigureAwait(false);

                while (true)
                {
                    if (Volatile.Read(ref _closed) != 0)
                        return;

                    int length;
                    lock (_sendLock)
                    {
                        length = _pendingLength;
                        if (length == 0)
                            break;

                        // Swap batch buffers: producers continue filling the other one while we send.
                        var batch = _pending;
                        _pending = sending.Length >= _options.SendBufferSize ? sending : Array.Empty<byte>();
                        if (_pending.Length == 0 && sending.Length > 0)
                            BufferPool.Return(sending);
                        _pendingLength = 0;
                        sending = batch;
                    }

                    _tap?.OnSending(new ReadOnlySpan<byte>(sending, 0, length));

                    int offset = 0;
                    while (offset < length)
                    {
                        int sent = await _socket.SendAsync(new ReadOnlyMemory<byte>(sending, offset, length - offset),
                            SocketFlags.None).ConfigureAwait(false);
                        if (sent <= 0)
                            throw new SocketException((int)SocketError.ConnectionReset);
                        offset += sent;
                    }
                }

                // Idle: give the in-flight buffer back so idle connections hold no send memory, and with
                // ReleaseIdleBuffers the empty batch buffer too (the next send rents one).
                if (sending.Length > 0)
                {
                    BufferPool.Return(sending);
                    sending = Array.Empty<byte>();
                }

                if (_options.ReleaseIdleBuffers)
                {
                    byte[] spare = Array.Empty<byte>();
                    lock (_sendLock)
                        if (_pendingLength == 0)
                            (spare, _pending) = (_pending, Array.Empty<byte>());
                    BufferPool.Return(spare);
                }
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            Volatile.Write(ref _closed, 1);
            BufferPool.Return(sending);
            byte[] unsent;
            int unsentLength;
            lock (_sendLock)
            {
                (unsent, unsentLength) = (_pending, _pendingLength);
                _pending = Array.Empty<byte>();
                _pendingLength = 0;
            }

            // Queued but never written: the tap still sees it (outside the lock: the tap may send on another connection).
            if (unsentLength > 0) _tap?.OnSending(new ReadOnlySpan<byte>(unsent, 0, unsentLength));
            BufferPool.Return(unsent);
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
                    var bigger = BufferPool.Rent(needed);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, filled);
                    BufferPool.Return(buffer);
                    buffer = bigger;
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
        needed = 0;
        while (filled - offset >= FrameHeader.Size)
        {
            var span = new ReadOnlySpan<byte>(buffer, offset, filled - offset);
            var header = FrameHeader.Read(span);
            if (header.Length < FrameHeader.Size || header.Length > _options.MaxFrameSize)
                throw new InvalidOperationException("Invalid frame length " + header.Length);

            if (header.Length > span.Length)
            {
                needed = header.Length;
                break;
            }

            _handler.OnFrame(this, in header, span.Slice(0, header.Length));
            offset += header.Length;
        }

        return offset;
    }
}
