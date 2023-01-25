using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Shared.Misc;

namespace DaServer.Shared.Network;

public class TcpServer
{
    /// <summary>
    /// 获取绑定终结点
    /// </summary>
    public IPEndPoint Ip => _ip;

    /// <summary>
    /// 监听地址
    /// </summary>
    private readonly IPEndPoint _ip;

    /// <summary>
    /// TCP监听Socket
    /// </summary>
    private readonly Socket _listener = null!;

    /// <summary>
    /// 是否被释放
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 客户端
    /// </summary>
    private readonly ConcurrentDictionary<ulong, TcpClient> _clients =
        new ConcurrentDictionary<ulong, TcpClient>();

    /// <summary>
    /// 专门用来启动标记的客户端
    /// </summary>
    private readonly ConcurrentQueue<TcpClient> _clientsToStart = new ConcurrentQueue<TcpClient>();

    /// <summary>
    /// 客户端连接回调
    /// </summary>
    public event Action<uint>? OnConnect;

    /// <summary>
    /// 客户端发来消息回调
    /// </summary>
    public event Action<uint, ReadOnlySequence<byte>>? OnMessage;

    /// <summary>
    /// 客户端断开回调
    /// </summary>
    public event Action<uint, string>? OnDisconnect;

    /// <summary>
    /// 是否在运行
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 获取客户端
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    private TcpClient? GetClient(uint id)
    {
        if (_clients.TryGetValue(id, out var client))
        {
            return client;
        }

        return null;
    }

    /// <summary>
    /// 客户端是否在线
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public bool ClientOnline(uint id)
    {
        var client = GetClient(id);
        if (client == null) return false;
        return client.Socket.Connected;
    }

    /// <summary>
    /// 把客户端踢下线
    /// </summary>
    /// <param name="id"></param>
    public void KickClient(uint id)
    {
        if (!_clients.ContainsKey(id)) return;
        _clients[id].Close("server kicked this client");
    }

    /// <summary>
    /// 给客户端发消息
    /// </summary>
    /// <param name="id"></param>
    /// <param name="message"></param>
    public void SendToClient(uint id, Span<byte> message)
    {
        if (!_clients.TryGetValue(id, out var client)) return;
        client.Send(message);
    }

    /// <summary>
    /// 给客户端发消息
    /// </summary>
    /// <param name="id"></param>
    /// <param name="message"></param>
    public ValueTask SendToClientAsync(uint id, Memory<byte> message)
    {
        if (!_clients.TryGetValue(id, out var client)) return ValueTask.CompletedTask;
        return client.SendAsync(message);
    }

    /// <summary>
    /// 初始化服务器
    /// </summary>
    public TcpServer(string ip, int port)
    {
        _disposed = false;
        IPEndPoint localEp = new IPEndPoint(IPAddress.Parse(ip), port);
        _ip = localEp;
        try
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Dispose();
        }
    }

    /// <summary>
    /// 启动
    /// </summary>
    public void Start()
    {
        _listener.Bind(_ip);
        _listener.Listen(3000);
        Logger.Info("Listen Tcp -> {Ip} ", Ip);
        IsRunning = true;
        //单独一个线程检测连接
        new Thread(AcceptAsync).Start();
        //单独一个线程检测客户端是否在线
        new Thread(CheckStatus).Start();
        //单独一个线程启动客户端
        new Thread(StartClients).Start();
        //单独一个线程处理GC
        new Thread(() =>
        {
            while (IsRunning)
            {
                GC.Collect(0, GCCollectionMode.Forced);
                GC.Collect(1, GCCollectionMode.Forced);
                GC.Collect(2, GCCollectionMode.Forced);
                Thread.Sleep(30000);
            }
        }).Start();
    }

    /// <summary>
    /// 循环监听状态  
    /// </summary>
    private void CheckStatus()
    {
        while (IsRunning)
        {
            //每10s 检查一次
            Thread.Sleep(1000 * 10);
            var ids = _clients.Keys.ToArray();
            foreach (uint id in ids)
            {
                if (!ClientOnline(id))
                {
                    GetClient(id)?.Close();
                }
            }
        }
    }

    /// <summary>
    /// 循环启动客户端
    /// </summary>
    private void StartClients()
    {
        while (IsRunning)
        {
            int cnt = _clientsToStart.Count;
            while (cnt-- > 0)
            {
                if (_clientsToStart.TryDequeue(out var client))
                {
                    client.Start();
                }
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// 当前连接的客户端id
    /// </summary>
    private uint _curId;

    /// <summary>
    /// 异步接收
    /// </summary>
    private async void AcceptAsync()
    {
        while (IsRunning)
        {
            try
            {
                var socket = await _listener.AcceptAsync().ConfigureAwait(false);
                Interlocked.Increment(ref _curId);
                var id = _curId;
                var client = new TcpClient(socket);
                if (_clients.TryAdd(id, client))
                {
                    client.OnReceived += arr => { OnMessage?.Invoke(id, arr); };
                    client.OnClose += msg =>
                    {
                        _clients.TryRemove(id, out _);
                        OnDisconnect?.Invoke(id, msg);
                    };
                    OnConnect?.Invoke(id);
                    _clientsToStart.Enqueue(client);
                }
                else
                {
                    Logger.Error(
                        "ERROR WITH Remote Socket LocalEndPoint：{socket.LocalEndPoint} RemoteEndPoint：{socket.RemoteEndPoint}",
                        socket.LocalEndPoint!, socket.RemoteEndPoint!);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            IsRunning = false;
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 释放所占用的资源
    /// </summary>
    /// <param name="dispose"></param>
    protected virtual void Dispose(bool dispose)
    {
        if (dispose)
        {
            try
            {
                Logger.Info("Stop Listener Tcp -> {0}:{1} ", Ip.Address, Ip.Port);
                _listener.Close();
                _listener.Dispose();
            }
            catch
            {
                //ignore
            }
        }
    }
}