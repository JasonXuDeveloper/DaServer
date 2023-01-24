using System;
using System.Threading.Tasks;
using DaServer.Shared.Network;

namespace DaServer.Shared.Core;

public class Session: IDisposable
{
    public uint Id { get; }
    public bool Connected => Server.ClientOnline(Id);
    private TcpServer Server { get; }

    public Session(uint id, TcpServer server)
    {
        Id = id;
        Server = server;
    }
    
    public void Dispose()
    {
        //关闭会话
        Server.KickClient(Id);
    }
    
    public void Send(Span<byte> data)
    {
        //发送数据
        Server.SendToClient(Id, data);
    }
    
    public async Task SendAsync(Memory<byte> data)
    {
        //异步发送数据
        await Server.SendToClientAsync(Id, data);
    }

    public void End()
    {
        Server.KickClient(Id);
        Dispose();
    }
}