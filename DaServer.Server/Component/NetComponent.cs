using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Extension;
using DaServer.Shared.Message;
using DaServer.Shared.Network;

namespace DaServer.Server.Component;

public class NetComponent : Shared.Core.Component
{
    public override ComponentRole Role => ComponentRole.LowLevel;

    /// <summary>
    /// tcp server
    /// </summary>
    private TcpServer _server = null!;

    /// <summary>
    /// sessions
    /// </summary>
    private ConcurrentDictionary<uint, Session> _sessions = null!;

    /// <summary>
    /// session request queue
    /// </summary>
    private ConcurrentQueue<(uint sessionId, RemoteCall call)> _queue = null!;

    /// <summary>
    /// on connect event
    /// </summary>
    public event Action<uint>? OnClientConnected;

    /// <summary>
    /// on disconnect event
    /// </summary>
    public event Action<uint, string>? OnClientDisconnected;

    /// <summary>
    /// on receive event
    /// </summary>
    public event Action<uint, ReadOnlySequence<byte>>? OnClientDataReceived;


    public override Task Create()
    {
        //init fields
        _sessions = new();
        _queue = new ConcurrentQueue<(uint sessionId, RemoteCall call)>();

        //create server
        _server = new TcpServer("0.0.0.0", 9999); //TODO: Config
        _server.OnConnect += id => OnClientConnected?.Invoke(id);
        _server.OnDisconnect += (id, reason) => OnClientDisconnected?.Invoke(id, reason);
        _server.OnMessage += (id, data) => OnClientDataReceived?.Invoke(id, data);

        //add callback
        OnClientConnected += id => { _sessions.TryAdd(id, new Session(id, _server)); };

        OnClientDisconnected += (id, _) =>
        {
            if (_sessions.TryRemove(id, out var session))
            {
                session.Dispose();
            }
        };

        OnClientDataReceived += (id, data) =>
        {
            //get session
            if (_sessions.TryGetValue(id, out _))
            {
                //process data
                _queue.Enqueue((id, MessageFactory.GetRemoteCall(data)));
            }
        };

        //start server
        _server.Start();

        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        _sessions.Clear();
        _server.Dispose();
        return Task.CompletedTask;
    }

    public override Task Update(int currentTick)
    {
        var msgProcComp = this.GetComponent<MessageComponent>()!;
        //fetch
        while (_queue.TryDequeue(out var queueItem))
        {
            //get session
            if (_sessions.TryGetValue(queueItem.sessionId, out var session))
            {
                //process
                msgProcComp.AddRequest(session, queueItem.call);
            }
        }

        return Task.CompletedTask;
    }
}