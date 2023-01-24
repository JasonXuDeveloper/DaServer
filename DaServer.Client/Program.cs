using System;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Client.Response;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using DaServer.Shared.Network;

namespace DaServer.Client;

public static class Program
{
    public static void Main(string[] args)
    {
        TcpClient client = new TcpClient("127.0.0.1", 9999);
        client.OnConnected += () =>
        {
            Logger.Info("客户端连上了服务端");
            Parallel.For(0, 100, async (i,__) =>
            {
                var response = await Request<MTestRequest, MTestResponse>(client, new MTestRequest()
                {
                    Txt = "hello"
                });
                Logger.Info("客户端收到了服务端的回应: {@c}", response);
                Logger.Info("多线程任务{i} 收到回应后的线程ID: {c}", i, Thread.CurrentThread.ManagedThreadId);
            });
        };
        client.OnClose += reason =>
        {
            Logger.Info("客户端断开了服务端: {@c}, {@r}", client, reason);
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

    /// <summary>
    /// 请求服务端接口，返回服务端的响应，全部返回结果必定会切换到同一个线程（不保证还在调用请求的线程）
    /// </summary>
    /// <param name="client"></param>
    /// <param name="request"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static async Task<TResponse> Request<TRequest, TResponse>(TcpClient client, TRequest request,
        float timeout = -1) where TRequest : IMessage where TResponse : IMessage
        => (TResponse)await Request(client, request, timeout);

    /// <summary>
    /// 请求服务端接口，返回服务端的响应，全部返回结果必定会切换到同一个线程（不保证还在调用请求的线程）
    /// </summary>
    /// <param name="client"></param>
    /// <param name="request"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public static async Task<IMessage> Request<T>(TcpClient client, T request, float timeout = -1) where T:IMessage
    {
        //请求
        var requestId = Interlocked.Increment(ref _requestId);
        //获取消息
        var buf = MessageFactory.GetMessage(requestId, request);
        //发送
        await client.SendAsync(buf);
        //接收处理
        var ret = await ResponseFactory.GetResponse(requestId, timeout);
        if (ret is null or MError)
        {
            throw new Exception("服务端的逻辑有错误");
        }
        return ret;
    }
}