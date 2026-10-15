using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Cluster;
using DaServer.Concurrency;
using DaServer.Net;
using DaServer.Protocol;
using DaServer.Rpc;
using Nino.Core;

namespace DaServer.Client;

/// <summary>
/// Client connection to a gateway (game client, bot, tool): sends <see cref="ClientMessageAttribute">[ClientMessage]</see>
/// types (<c>client.Send(new Move(..))</c>, <c>await client.Ask(new GetInfo())</c>) and dispatches the messages servers
/// push to the [Handle] methods of registered handler objects (<see cref="Register"/>).
/// </summary>
public sealed class ClusterClient : IMessageSender, IConnectionHandler, IAsyncDisposable
{
    private readonly PendingCallTable _pending = new(14);
    private readonly Lock _handlersGate = new();
    private Dictionary<uint, (object Target, MessageDispatcher Dispatcher)> _handlers = new(); // copy on write
    private readonly MpmcQueue<FrameWriter>? _inbox;
    private Connection? _connection;

    // One timer expires the calls of every client in the process (a bot process runs thousands of clients).
    private static readonly ConcurrentDictionary<ClusterClient, bool> Live = new();
    private static readonly Timer Sweeper = new(static _ =>
    {
        foreach (var (client, _) in Live) client._pending.SweepExpired();
    }, null, 100, 100);

    /// <param name="queueCallbacks">
    /// false: callbacks run on the IO thread as frames arrive (bots, tools). true: frames are queued and run by
    /// <see cref="ProcessCallbacks"/> on the application's own thread (game loop / UI thread).
    /// </param>
    public ClusterClient(bool queueCallbacks = false)
    {
        if (queueCallbacks) _inbox = new MpmcQueue<FrameWriter>(64 * 1024);
    }

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsConnected => _connection is { IsClosed: false };

    public event Action<Exception?>? Disconnected;

    /// <summary>
    /// Connect to the cluster through any gateway (typically a balancer's well-known address): the gateway names
    /// the least loaded gateway and the client moves there when it is another one. <paramref name="followBalancer"/>
    /// false stays on the given gateway. While the cluster is still forming (the balancer has not met a gateway yet) the
    /// question is asked again until <see cref="CallTimeout"/>.
    /// </summary>
    public async ValueTask ConnectAsync(string host, int port, bool followBalancer = true, CancellationToken cancellationToken = default)
    {
        await ConnectToAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (!followBalancer) return;

        var gateway = await PickGatewayAsync(cancellationToken).ConfigureAwait(false);
        if (gateway.Host == host && gateway.Port == port) return;

        var balancer = Interlocked.Exchange(ref _connection, null); // closed on purpose: not a disconnect
        balancer?.Close();
        await ConnectToAsync(gateway.Host, gateway.Port, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GatewayEndpoint> PickGatewayAsync(CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)CallTimeout.TotalMilliseconds;
        while (true)
        {
            var call = PendingCall<GatewayEndpoint>.Rent(null);
            var answer = call.Task;
            Send(FrameWriter.Rent(FrameHeader.Size), ControlOp.PickGateway, 0, call, FrameKind.Control);
            try
            {
                return await answer.ConfigureAwait(false);
            }
            catch (RpcException e) when (e.Status == RpcStatus.NotHosted && Environment.TickCount64 < deadline)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ConnectToAsync(string host, int port, CancellationToken cancellationToken)
    {
        var connection = await Connection.ConnectAsync(host, port, this, ConnectionOptions.ForClients(), cancellationToken)
            .ConfigureAwait(false);
        _connection = connection;
        GC.KeepAlive(Sweeper);
        Live.TryAdd(this, true);
    }

    /// <summary>
    /// Register an object whose [Handle] methods receive the messages servers push to this client (one handler per
    /// message type: a later registration replaces an earlier one).
    /// </summary>
    public void Register(object handler)
    {
        var handlers = MessageRegistry.HandlersOf(handler.GetType());
        lock (_handlersGate)
        {
            var next = new Dictionary<uint, (object, MessageDispatcher)>(_handlers);
            foreach (var h in handlers.All) next[h.MessageId] = (handler, h.Dispatcher);
            Volatile.Write(ref _handlers, next);
        }
    }

    /// <summary>Run queued callbacks on the calling thread (only when constructed with queueCallbacks: true).</summary>
    public int ProcessCallbacks(int max = int.MaxValue)
    {
        if (_inbox == null) return 0;
        int n = 0;
        while (n < max && _inbox.TryDequeue(out var frame))
        {
            try
            {
                DispatchCallback(frame.Frame);
            }
            finally
            {
                frame.Dispose();
            }

            n++;
        }

        return n;
    }

    // ------------------------------------------------------------------ IMessageSender

    /// <summary>A [ClientMessage] to the actor of <paramref name="key"/> (AnyKey messages: the client chooses the key).</summary>
    public void Send<T>(long key, in T message) where T : struct => Send(MessageWriter.Write(in message), MessageType<T>.Id, key, null);

    /// <summary>A [ClientMessage] to its service, or to the actor bound to this client's session.</summary>
    public void Send<T>(in T message) where T : struct => Send(MessageWriter.Write(in message), MessageType<T>.Id, 0, null);

    public ValueTask<TReply> Ask<TRequest, TReply>(long key, in TRequest request) where TRequest : struct, IRequest<TReply>
    {
        var call = PendingCall<TReply>.Rent(null);
        var task = call.Task;
        Send(MessageWriter.Write(in request), MessageType<TRequest>.Id, key, call);
        return task;
    }

    public ValueTask<TReply> Ask<TRequest, TReply>(in TRequest request) where TRequest : struct, IRequest<TReply> =>
        Ask<TRequest, TReply>(0, in request);

    private void Send(FrameWriter request, uint messageId, long key, IPendingCall? call, FrameKind requestKind = FrameKind.Request)
    {
        var connection = _connection;
        try
        {
            if (connection == null || connection.IsClosed)
                throw new RpcException(RpcStatus.Disconnected);

            uint requestId = call != null ? _pending.Register(call, (int)CallTimeout.TotalMilliseconds) : 0;
            request.SetHeader(call != null ? requestKind : FrameKind.OneWay, requestId, messageId, key,
                call != null && requestKind == FrameKind.Request ? FrameHeader.EncodeBudget((long)CallTimeout.TotalMilliseconds) : RpcStatus.Ok);
            if (!connection.Send(request.Frame) && call != null)
                _pending.Take(requestId)?.Fail(new RpcException(RpcStatus.Disconnected));
        }
        catch (RpcException e)
        {
            call?.Fail(e);
        }
        finally
        {
            request.Dispose();
        }
    }

    // ------------------------------------------------------------------ IConnectionHandler

    void IConnectionHandler.OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        switch (header.Kind)
        {
            case FrameKind.Response:
                _pending.TryComplete(header.RequestId, header.Status, frame.Slice(FrameHeader.Size));
                break;
            case FrameKind.Push:
            case FrameKind.Broadcast:
                if (_inbox != null) _inbox.TryEnqueue(FrameWriter.CopyOf(frame));
                else DispatchCallback(frame);
                break;
        }
    }

    void IConnectionHandler.OnClosed(Connection connection, Exception? error)
    {
        if (connection != _connection) return; // the balancer connection, left on purpose
        _pending.FailAll(RpcStatus.Disconnected);
        Disconnected?.Invoke(error);
    }

    private void DispatchCallback(ReadOnlySpan<byte> frame)
    {
        var header = FrameHeader.Read(frame);
        if (!Volatile.Read(ref _handlers).TryGetValue(header.MessageId, out var handler)) return;
        var reader = new Reader(frame.Slice(FrameHeader.Size));
        var context = new CallContext(null, 0, header.MessageId, header.Key, default);
        var task = handler.Dispatcher(handler.Target, ref reader, in context);
        if (task.IsCompleted) task.GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Live.TryRemove(this, out _);
        _connection?.Close();
        if (_inbox != null)
            while (_inbox.TryDequeue(out var frame)) frame.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
