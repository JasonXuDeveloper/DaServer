using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Misc;
using DaServer.Shared.Request;

namespace DaServer.Shared.Component;

public class ActorComponent: Core.Component
{
    internal override ComponentRole Role => ComponentRole.LowLevel;
    
    private ConcurrentDictionary<Session, Actor> _actors  = null!;
    private List<Actor> _actorList  = null!;
    private List<Task> _tasks  = null!;

    public override Task Create()
    {
        _actors = new ConcurrentDictionary<Session, Actor>();
        _actorList = new List<Actor>();
        _tasks = new List<Task>();
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        _actors.Clear();
        _actorList.Clear();
        return Task.CompletedTask;
    }
    
    public Actor AddActor(Session session)
    {
        Actor actor = new Actor(session);
        _actors.TryAdd(session, actor);
        _actorList.Add(actor);
        return actor;
    }
    
    public void RemoveActor(Session session)
    {
        _actors.TryRemove(session, out _);
        _actorList.RemoveAll(x => x.Session == session);
    }

    public void RemoveActor(Actor actor)
    {
        foreach (var pair in _actors)
        {
            if (pair.Value == actor)
            {
                _actors.TryRemove(pair.Key, out _);
                _actorList.Remove(actor);
                //TODO 关闭该会话
                break;
            }
        }
    }
    
    public Actor? GetActor(Session session)
    {
        _actors.TryGetValue(session, out var actor);
        return actor;
    }

    public override async Task Update(int currentTick)
    {
        //循环每个Actor并调用Request
        int cnt = _actorList.Count;
        _tasks.Clear();
        for (int i = 0; i < cnt; i++)
        {
            //按顺序处理每个actor的消息
            Actor actor = _actorList[i];
            //TODO 检测actor的session断开

            while (actor.Requests.TryDequeue(out var remoteCall))
            {
                var requestTask = RequestFactory.GetRequest(remoteCall.MsgId);
                if(requestTask == null)
                    continue;
                var task = requestTask.OnRequest(actor, remoteCall.MessageObj!);
                _tasks.Add(task.ContinueWith((t, __) =>
                {
                    //不等待响应结果
                    _ = actor.Respond(remoteCall.RequestId, t.Result);
                }, null));
            }
        }
        await Task.WhenAll(_tasks);
    }
}