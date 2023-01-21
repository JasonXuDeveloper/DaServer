using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Shared.Core;

public class Actor
{
    public Session Session;

    public readonly ConcurrentQueue<RemoteCall> Requests = new();

    public void AddRequest(RemoteCall call)
    {
        Requests.Enqueue(call);
    }
    
    public Actor(Session session)
    {
        Session = session;
    }
    
    /// <summary>
    /// 回复某个请求
    /// </summary>
    /// <param name="requestId"></param>
    /// <param name="val"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task Respond<T>(int requestId, T? val) where T: IMessage
    {
        //如果是空，则请求出了问题，抛异常
        if (val is null)
        {
            // 发送错误
            await Session.SendAsync(MessageFactory.GetMessage(requestId, MError.Empty));
            return;
        }
        // 发送任务
        var buf = MessageFactory.GetMessage(requestId, val);
        await Session.SendAsync(buf);
    }


    public void ChangeSession(Session newSession)
    {
        //TODO 老Session下线
        
        Session = newSession;
    }
}