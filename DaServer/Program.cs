using System;
using DaServer;
using DaServer.Shared.Component;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using DaServer.Shared.Network;

public static class Program
{
    public static void Main(string[] args)
    {
        var sys = new DaServer.Shared.Core.System();
        sys.AddComponent<NetComponent>();
        sys.AddComponent<MessageComponent>();
        sys.AddComponent<ActorComponent>();

        TcpClient client = new TcpClient("127.0.0.1", 9999);
        client.OnConnected += () =>
        {
            Logger.Info("客户端连上了服务端: {c}", client);
            var buf = MessageFactory.GetMessage(new MTestRequest(), 1);
            client.SendAsync(buf).Wait();
            Logger.Info("发了消息 {buf}", new ArraySegment<byte>(buf));
        };
        client.OnClose += reason =>
        {
            Logger.Info("客户端断开了服务端: {c}, {r}", client, reason);
        };
        client.Connect();
        while (true)
        {
            Console.ReadKey();
            client.Close();
        }
    }
}