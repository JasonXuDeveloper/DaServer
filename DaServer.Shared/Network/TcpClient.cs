using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DaServer.Shared.Misc;
using Pipelines.Sockets.Unofficial;

namespace DaServer.Shared.Network;

public class TcpClient
{
    /// <summary>
    /// 连上的回调
    /// </summary>
    public event Action? OnConnected;

    /// <summary>
    /// 收到服务端消息的回调
    /// </summary>
    public event Action<ReadOnlySequence<byte>>? OnReceived;

    /// <summary>
    /// 连接的IP
    /// </summary>
    public readonly IPAddress Ip;

    /// <summary>
    /// 连接的端口
    /// </summary>
    public readonly int Port;

    /// <summary>
    /// 封装socket
    /// </summary>
    internal readonly Socket Socket;

    /// <summary>
    /// 封装管道
    /// </summary>
    private readonly Pipe _pipe;

    //标识是否已经释放
    private volatile bool _isDispose;

    //默认10K的缓冲区空间
    private readonly int _bufferSize = 10 * 1024;

    //断开回调
    public event Action<string>? OnClose;

    /// <summary>
    /// 客户端主动请求服务器
    /// </summary>
    /// <param name="ip"></param>
    /// <param name="port"></param>
    public TcpClient(string ip, int port)
    {
        Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Socket.NoDelay = true;
        Socket.ReceiveTimeout = 1000 * 60 * 5; //5分钟没收到东西就算超时
        Ip = IPAddress.Parse(ip);
        Port = port;
        _pipe = new Pipe();
    }

    /// <summary>
    /// 连接
    /// </summary>
    public void Connect()
    {
        if (Socket.Connected) return;
        Socket.Connect(Ip, Port);
        Start();
    }

    /// <summary>
    /// 这个是服务器收到有效链接初始化
    /// </summary>
    /// <param name="socket"></param>
    internal TcpClient(Socket socket)
    {
        Socket = socket;
        IPEndPoint? remoteIpEndPoint = socket.RemoteEndPoint as IPEndPoint;
        Ip = remoteIpEndPoint?.Address!;
        Port = remoteIpEndPoint?.Port ?? 0;
        _pipe = new Pipe();
    }

    /// <summary>
    /// 开始监听
    /// </summary>
    internal void Start()
    {
        SetSocket();
        Task writing = FillPipeAsync(Socket, _pipe.Writer);
        Task reading = ReadPipeAsync(_pipe.Reader);
        _ = Task.WhenAll(reading, writing);
        OnConnected?.Invoke();
    }

    /// <summary>
    /// 设置socket
    /// </summary>
    private void SetSocket()
    {
        _isDispose = false;
        Socket.ReceiveBufferSize = _bufferSize;
        Socket.SendBufferSize = _bufferSize;
    }

    /// <summary>
    /// Read from socket and write to pipe
    /// </summary>
    /// <param name="socket"></param>
    /// <param name="writer"></param>
    async Task FillPipeAsync(Socket socket, PipeWriter writer)
    {
        try
        {
            while (!_isDispose && Socket.Connected)
            {
                // Allocate at least _bufferSize bytes from the PipeWriter.
                Memory<byte> memory = writer.GetMemory(_bufferSize);
                try
                {
                    int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    // Tell the PipeWriter how much was read from the Socket.
                    writer.Advance(bytesRead);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "NET Error");
                    break;
                }

                // Make the data available to the PipeReader.
                FlushResult result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (SocketException)
        {
            Close("connection has been closed");
        }
        catch (ObjectDisposedException)
        {
            Close("connection has been closed");
        }
        catch (Exception ex)
        {
            Close($"{ex.Message}\n{ex.StackTrace}");
        }

        // By completing PipeWriter, tell the PipeReader that there's no more data coming.
        await writer.CompleteAsync();
        Logger.Info("不接收了");
    }

    /// <summary>
    /// Read from pipe and process
    /// </summary>
    /// <param name="reader"></param>
    async Task ReadPipeAsync(PipeReader reader)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TryParsePacket(ref buffer, out ReadOnlySequence<byte> packet))
            {
                // Process callback
                try
                {
                    OnReceived?.Invoke(packet);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "NET Error");
                }
            }

            // Tell the PipeReader how much of the buffer has been consumed.
            reader.AdvanceTo(buffer.Start, buffer.End);

            // Stop reading if there's no more data coming.
            if (result.IsCompleted)
            {
                break;
            }
        }

        // Mark the PipeReader as complete.
        await reader.CompleteAsync();
    }

    bool TryParsePacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet)
    {
        //first 4 bytes is a uint represents length of packet
        if (buffer.Length < 4)
        {
            packet = default;
            return false;
        }

        //read length (this length includes the length of the length, 4 bytes)
        var firstByte = buffer.FirstSpan.GetPinnableReference();
        uint length = Unsafe.As<byte, uint>(ref firstByte);

        // Read the packet
        if (buffer.Length < length)
        {
            packet = default;
            return false;
        }

        packet = buffer.Slice(4, length - 4);
        buffer = buffer.Slice(length);
        return true;
    }
    
    /// <summary>
    /// 发送消息方法
    /// </summary>
    public unsafe void Send(Span<byte> buffer)
    {
        try
        {
            if (!_isDispose)
            {
                //长度作为uint放在前4个字节
                uint length = (uint)buffer.Length + 4;
                //uint取其span
                Span<byte> lengthSpan = new Span<byte>(&length, 4);
                Socket.Send(lengthSpan);
                Socket.Send(buffer);
            }
        }
        catch
        {
            Close("connection has been closed");
        }
    }

    /// <summary>
    /// 发送消息方法
    /// </summary>
    public async Task SendAsync(Memory<byte> buffer)
    {
        try
        {
            if (!_isDispose)
            {
                //长度作为uint放在前4个字节
                uint length = (uint)buffer.Length + 4;
                Memory<byte> memory;
                unsafe
                {
                    using var memoryManager = new UnmanagedMemoryManager<byte>((byte*)&length, 4);
                    memory = memoryManager.Memory;
                }

                await Socket.SendAsync(memory);
                await Socket.SendAsync(buffer);
            }
        }
        catch
        {
            Close("connection has been closed");
        }
    }

    /// <summary>
    /// 关闭并释放资源
    /// </summary>
    /// <param name="msg"></param>
    public void Close(string msg = "closed manually")
    {
        if (!_isDispose)
        {
            _isDispose = true;
            try
            {
                try
                {
                    Socket.Close();
                }
                catch
                {
                    //ignore
                }

                Socket.Dispose();
                GC.SuppressFinalize(this);
            }
            catch (Exception)
            {
                //ignore
            }

            OnClose?.Invoke(msg);
        }
    }
}