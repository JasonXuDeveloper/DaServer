using System;
using System.Threading.Tasks;
using DaServer.Shared.Network;

namespace DaServer.Shared.Core;

public class Session: IDisposable
{
    public uint Id;
    private readonly TcpServer _server;
    
    public Session(uint id, TcpServer server)
    {
        Id = id;
        _server = server;
    }
    
    public void Dispose()
    {
        //关闭会话
        _server.KickClient(Id);
    }
    
    public void Send(Span<byte> data)
    {
        //发送数据
        _server.SendToClient(Id, data);
    }
    
    public async Task SendAsync(Memory<byte> data)
    {
        //异步发送数据
        await _server.SendToClientAsync(Id, data);
    }
}