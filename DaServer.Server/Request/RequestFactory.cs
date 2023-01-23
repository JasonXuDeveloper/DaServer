using System;
using System.Collections.Concurrent;
using System.Linq;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Server.Request;

public static class RequestFactory
{
    /// <summary>
    /// 缓存
    /// </summary>
    private static readonly ConcurrentDictionary<int, IRequest> Requests = new ConcurrentDictionary<int, IRequest>();

    static RequestFactory()
    {
        LoadRequests();
    }

    public static void Reload() => LoadRequests();

    private static void LoadRequests()
    {
        //反射全部类型
        var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes());
        //获取所以继承Request<,,>的类型
        var requestTypes = types.Where(t => t.BaseType is { IsClass: true, IsGenericType: true } && t.BaseType.GetGenericTypeDefinition() == typeof(Request<,,>));
        //遍历所有类型
        foreach (var type in requestTypes)
        {
            //获取消息泛型类型
            var msgType = type.BaseType!.GetGenericArguments()[1];
            //获取消息ID
            var id = MessageFactory.GetMsgId(msgType);
            //创建实例
            var request = (IRequest)Activator.CreateInstance(type)!;
            //添加到缓存
            Requests[id] = request;
            //返回类型
            var returnType = type.BaseType.GetGenericArguments()[2];
            Logger.Info("注册了请求「msgId={id} ({msgType}) => msgId={id2} ({msgType2})」：{tName}", id,
                MessageFactory.GetMsgType(id)!, MessageFactory.GetMsgId(returnType),
                returnType.FullName!, type.FullName!);
        }
    }
    
    public static IRequest? GetRequest(int msgId)
    {
        if (Requests.TryGetValue(msgId, out var request))
        {
            return request;
        }
        return null;
    }
}