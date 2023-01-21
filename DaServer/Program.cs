using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer;
using DaServer.Shared.Component;
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
            _ = Request(client, 1, new MTestRequest()
            {
                Txt = "hello"
            }).ContinueWith((t, _) =>
            {
                Logger.Info("客户端收到了服务端的回应: {c}", t.Result);
                MTestResponse response = (MTestResponse)t.Result;
                Logger.Info("response.txt: {c}", response.Txt);
            }, null);
        };
        client.OnClose += reason =>
        {
            Logger.Info("客户端断开了服务端: {c}, {r}", client, reason);
        };
        client.OnReceived += data =>
        {
            var remoteCall = MessageFactory.GetRemoteCall(data);
            Logger.Info("客户端收到了服务端的消息: {r}", remoteCall);
            if(Requests.TryGetValue(remoteCall.RequestId, out var tcs))
            {
                tcs.SetResult(remoteCall.MessageObj!);
                Requests.TryRemove(remoteCall.RequestId, out _);
            }
        };

        client.Connect();
        while (true)
        {
            Console.ReadKey();
            client.Close();
        }
    }
    
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<IMessage>> Requests = new ConcurrentDictionary<int, TaskCompletionSource<IMessage>>();

    private static async Task<IMessage> Request<T>(TcpClient client, int requestId, T request) where T:IMessage
    {
        var buf = MessageFactory.GetMessage(requestId, request);
        Logger.Info("准备发消息 {buf}", new ArraySegment<byte>(buf));
        Requests[requestId] = new TaskCompletionSource<IMessage>();
        await client.SendAsync(buf);
        return await Requests[requestId].Task;
    }
}