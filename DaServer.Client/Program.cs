using System;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Client.Response;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using DaServer.Shared.Network;
using Nito.AsyncEx;

namespace DaServer.Client;

public static class Program
{
    public static void Main(string[] args)
    {
        TcpClient client = new TcpClient("127.0.0.1", 9999);
        client.OnConnected += async () =>
        {
            Logger.Info("客户端连上了服务端");
            for (int i = 0; i < 30; i++)
            {
                var response = (MTestResponse)await Request(client, new MTestRequest()
                {
                    Txt = "hello"
                });
                Logger.Info("客户端收到了服务端的回应: {c}", response);
                Logger.Info("response.txt: {c}", response.Txt);
                Logger.Info("thread id: {c}", Thread.CurrentThread.ManagedThreadId);
            }
        };
        client.OnClose += reason =>
        {
            Logger.Info("客户端断开了服务端: {c}, {r}", client, reason);
        };
        client.OnReceived += data =>
        {
            var remoteCall = MessageFactory.GetRemoteCall(data);
            Logger.Info("客户端收到了服务端的消息: {@r}", remoteCall);
            ResponseFactory.AddResponse(ref remoteCall);
        };

        client.Connect();
        while (true)
        {
            Console.ReadKey();
            client.Close();
        }
    }

    private static int _requestId;
    
    private static async Task<IMessage> Request<T>(TcpClient client, T request, float timeout = -1) where T:IMessage
    {
        //请求
        var requestId = Interlocked.Increment(ref _requestId);
        //获取消息
        var buf = MessageFactory.GetMessage(requestId, request);
        //接收处理
        RemoteCall? remoteCall = null;
        //异步上下文
        using (var ctx = new AsyncContext())
        {
            //开始执行
            ctx.SynchronizationContext.OperationStarted();
            //派发异步任务
            ctx.SynchronizationContext.Post(async _ =>
            {
                //发送
                await client.SendAsync(buf);
                //等待回复
                remoteCall = await ResponseFactory.GetResponse(requestId, timeout);
                //通知完成
                ctx.SynchronizationContext.OperationCompleted();
            }, null);
            //执行，在被通知前不会退出
            ctx.Execute();
        }

        ResponseFactory.RemoveResponse(requestId);
        //返回
        var ret = remoteCall?.MessageObj;
        if (ret is null or MError)
        {
            throw new Exception("服务端的逻辑有错误");
        }
        Logger.Info("{r}", ret);
        return ret;
    }
}