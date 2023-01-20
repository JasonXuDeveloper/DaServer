using System;
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
}