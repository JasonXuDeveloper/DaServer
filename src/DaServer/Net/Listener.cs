namespace DaServer.Net;

/// <summary>Accepts TCP connections and wraps them in <see cref="Connection"/>s.</summary>
internal sealed class Listener : IDisposable
{
    private readonly Socket _socket;
    private readonly Func<Socket, Connection> _accept;
    private volatile bool _disposed;

    /// <param name="accept">Creates the connection for an accepted socket (not started yet).</param>
    public Listener(IPEndPoint endPoint, Func<Socket, Connection> accept, int backlog = 4096)
    {
        _accept = accept;
        _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
            _socket.DualMode = true;
        // No ReuseAddress: on Linux .NET maps it to SO_REUSEPORT, which lets a second process bind the same port and
        // silently take half of the connections. Rebinding a port in TIME_WAIT works without it on every platform.
        _socket.Bind(endPoint);
        _socket.Listen(backlog);
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    /// <summary>Bound endpoint (resolves port 0 to the real port).</summary>
    public IPEndPoint LocalEndPoint { get; }

    public void Start() => _ = AcceptLoop();

    private async Task AcceptLoop()
    {
        while (!_disposed)
        {
            Socket socket;
            try
            {
                socket = await _socket.AcceptAsync().ConfigureAwait(false);
            }
            catch when (_disposed)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // transient accept failure (e.g. aborted handshake)
            }

            try
            {
                _accept(socket).Start();
            }
            catch
            {
                socket.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }
}
