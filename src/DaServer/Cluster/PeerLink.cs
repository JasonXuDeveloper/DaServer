using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Buffers;
using DaServer.Net;
using DaServer.Protocol;
using DaServer.Rpc;
using DaServer.Runtime;
using Microsoft.Extensions.Logging;

namespace DaServer.Cluster;

/// <summary>
/// Outgoing link to another node (one per remote endpoint). Connects lazily, buffers frames while connecting
/// (bounded), reconnects with backoff. Everything this node sends to that node travels on this link: requests, one-way
/// calls, events, pushes, session control and the replies to that node's requests (the peer names itself with a
/// <see cref="ControlOp.Hello"/>), so one ordered stream carries them: a reply never overtakes the Bind sent before it.
/// </summary>
internal sealed class PeerLink : IDisposable
{
    private const int MaxBacklogBytes = 64 * 1024 * 1024;

    private readonly Node _node;
    private readonly Lock _gate = new();
    private Connection? _connection;
    private List<FrameWriter>? _backlog;
    private int _backlogBytes;
    private bool _connecting;
    private bool _disposed;

    public PeerLink(Node node, string host, int port)
    {
        _node = node;
        Host = host;
        Port = port;
        ReplySink = new LinkReplySink(this);
    }

    /// <summary>Replies to requests that node sent us (they travel with everything else we send it).</summary>
    public IReplySink ReplySink { get; }

    public string Host { get; }
    public int Port { get; }

    public bool IsConnected => Volatile.Read(ref _connection) is { IsClosed: false };

    /// <summary>Send a complete frame (copied). Thread-safe, never blocks.</summary>
    public void Send(ReadOnlySpan<byte> frame)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection != null && connection.Send(frame))
            return;

        lock (_gate)
        {
            connection = _connection;
            if (connection != null && connection.Send(frame))
                return;
            if (_disposed)
                return;

            if (_backlogBytes + frame.Length > MaxBacklogBytes)
            {
                _node.Logger.LogWarning("Link {Host}:{Port} backlog full, dropping frame", Host, Port);
                return;
            }

            (_backlog ??= new()).Add(FrameWriter.CopyOf(frame));
            _backlogBytes += frame.Length;
            if (!_connecting)
            {
                _connecting = true;
                _ = ConnectLoop();
            }
        }
    }

    private async Task ConnectLoop()
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(Host).ConfigureAwait(false);
                var connection = await Connection.ConnectAsync(new IPEndPoint(addresses[0], Port), _node.ClusterHandler,
                    ConnectionOptions.ForNodeLinks()).ConfigureAwait(false);
                connection.Tag = this;

                connection.Send(_node.HelloFrame());
                lock (_gate)
                {
                    if (_backlog != null)
                    {
                        foreach (var frame in _backlog)
                        {
                            connection.Send(frame.Frame);
                            frame.Dispose();
                        }

                        _backlog = null;
                        _backlogBytes = 0;
                    }

                    Volatile.Write(ref _connection, connection);
                    _connecting = false;
                }

                return;
            }
            catch (Exception e)
            {
                attempt++;
                bool giveUp;
                lock (_gate) giveUp = _disposed || attempt >= 10;
                if (giveUp)
                {
                    _node.Logger.LogWarning(e, "Link {Host}:{Port} unreachable, dropping backlog", Host, Port);
                    DropBacklog();
                    return;
                }

                await Task.Delay(Math.Min(2000, 50 << Math.Min(attempt, 6))).ConfigureAwait(false);
            }
        }
    }

    internal void OnConnectionClosed(Connection connection, Exception? error)
    {
        lock (_gate)
        {
            if (_connection != connection) return;
            Volatile.Write(ref _connection, null);
        }

        // Calls in flight on this link cannot complete any more (until resumable links, see ADR-008).
        _node.Pending.FailOwnedBy(this, Protocol.RpcStatus.Disconnected);
        if (!_disposed)
            _node.Logger.LogInformation("Link {Host}:{Port} closed: {Reason}", Host, Port, error?.Message ?? "remote close");
    }

    private void DropBacklog()
    {
        lock (_gate)
        {
            if (_backlog != null)
                foreach (var frame in _backlog)
                    frame.Dispose();
            _backlog = null;
            _backlogBytes = 0;
            _connecting = false;
        }

        _node.Pending.FailOwnedBy(this, Protocol.RpcStatus.Disconnected);
    }

    public void Dispose()
    {
        Connection? connection;
        lock (_gate)
        {
            _disposed = true;
            connection = _connection;
        }

        connection?.Close();
        DropBacklog();
    }
}

/// <summary>Sends replies through a <see cref="PeerLink"/> (reconnects and backlog included).</summary>
internal sealed class LinkReplySink(PeerLink link) : IReplySink
{
    public void SendReply(uint requestId, RpcStatus status, FrameWriter response)
    {
        response.SetHeader(FrameKind.Response, requestId, 0, 0, status);
        link.Send(response.Frame);
        response.Dispose();
    }
}
