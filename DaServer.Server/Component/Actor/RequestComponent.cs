using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Server.Request;
using DaServer.Shared.Core;
using Nino.Shared.IO;

namespace DaServer.Server.Component;

/// <summary>
/// 处理请求的组件
/// </summary>
public class RequestComponent: ActorComponent
{
    /// <summary>
    /// 确保不会被删除该组件
    /// </summary>
    public override ComponentRole Role => ComponentRole.LowLevel;

    private ConcurrentQueue<RemoteCall> _requests = new();

    public override Task Create()
    {
        _requests = new ConcurrentQueue<RemoteCall>();
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        _requests.Clear();
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// 添加需要派发的请求
    /// </summary>
    /// <param name="call"></param>
    public void AddRequest(RemoteCall call)
    {
        _requests.Enqueue(call);
    }

    public override Task Update(long currentMs)
    {
        var tasks = ObjectPool<List<Task>>.Request();
        tasks.Clear();
        while (_requests.TryDequeue(out var remoteCall))
        {
            var requestTask = RequestFactory.GetRequest(remoteCall.MsgId);
            if(requestTask == null)
                continue;
            var task = requestTask.OnRequest(Actor, remoteCall.MessageObj!);
            tasks.Add(task.ContinueWith((t, __) =>
            {
                //不等待响应结果
                _ = Actor.Respond(remoteCall.RequestId, t.Result);
            }, null));
        }
        
        if (tasks.Count > 0)
        {
            return Task.WhenAll(tasks).ContinueWith((_, _) =>
            {
                tasks.Clear();
                ObjectPool<List<Task>>.Return(tasks);
            }, null);
        }

        return Task.CompletedTask;
    }
}