using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using DaServer.Shared.Network;

namespace DaServer.Client;

public static class Program
{
    public static void Main(string[] args)
    {
        TcpClient client = new TcpClient("127.0.0.1", 9999);
        client.OnConnected += async () =>
        {
            Logger.Info("客户端连上了服务端");
            var response = (MTestResponse)await Request(client, new MTestRequest()
            {
                Txt = "hello"
            });
            Logger.Info("客户端收到了服务端的回应: {c}", response);
            Logger.Info("response.txt: {c}", response.Txt);
        };
        client.OnClose += reason =>
        {
            Logger.Info("客户端断开了服务端: {c}, {r}", client, reason);
        };
        client.OnReceived += data =>
        {
            var remoteCall = MessageFactory.GetRemoteCall(data);
            Logger.Info("客户端收到了服务端的消息: {@r}", remoteCall);
            //id > 0 => response, 0 => callback
            if (remoteCall.RequestId > 0)
            {
                if(Requests.TryGetValue(remoteCall.RequestId, out var tcs))
                {
                    if (remoteCall.MsgId == MsgId.Error)
                    {
                        tcs.SetException(new Exception("服务端逻辑发生错误"));
                    }
                    else
                    {
                        tcs.SetResult(remoteCall.MessageObj!);
                    }
                }
            }
            else
            {
                //TODO 派发客户端事件
            }
        };

        client.Connect();
        while (true)
        {
            Console.ReadKey();
            client.Close();
        }
    }

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<IMessage>>
        Requests = new();
    private static int _requestId;
    
    private static async Task<IMessage> Request<T>(TcpClient client, T request) where T:IMessage
    {
        var requestId = Interlocked.Increment(ref _requestId);
        var buf = MessageFactory.GetMessage(requestId, request);
        TaskCompletionSource<IMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.TryAdd(requestId, tcs);
        await client.SendAsync(buf);
        //TODO 调度回上文线程
        var ret = await tcs.Task;
        Requests.TryRemove(requestId, out _);
        if (ret is null or MError)
        {
            throw new Exception("服务端的逻辑有错误");
        }
        return ret;
    }
}