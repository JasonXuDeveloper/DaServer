using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Message;

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
    public Task Respond<T>(int requestId, T? val) where T: IMessage
    {
        //TODO 如果是空，通知请求失败
        //TODO 发送任务
        return Task.CompletedTask;
    }


    public void ChangeSession(Session newSession)
    {
        //TODO 老Session下线
        
        Session = newSession;
    }
}