using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Core;
using DaServer.Shared.Misc;
using Nino.Shared.IO;

namespace DaServer.Server.Component;

public class ActorProcessComponent: Shared.Core.Component
{
    public override ComponentRole Role => ComponentRole.LowLevel;
    
    private ConcurrentDictionary<Session, Actor> _actors  = null!;
    private List<Actor> _actorList  = null!;

    public override Task Create()
    {
        _actors = new ConcurrentDictionary<Session, Actor>();
        _actorList = new List<Actor>();
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
        Actor actor = new Actor((Shared.Core.System)Holder, session);
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
                break;
            }
        }
    }
    
    public Actor? GetActor(Session session)
    {
        _actors.TryGetValue(session, out var actor);
        return actor;
    }

    public override async Task Update(long currentMs)
    {
        //循环每个Actor并调用Request
        int cnt = _actorList.Count;
        var tasks = ObjectPool<List<Task>>.Request();
        tasks.Clear();
        for (int i = 0; i < cnt; i++)
        {
            //按顺序处理每个actor的消息
            Actor actor = _actorList[i];
            var cur = Time.CurrentMs;
            int compCnt = actor.Components.Count;
            for (int j = 0; j < compCnt; j++)
            {
                if (j >= actor.Components.Count) break;
                var component = actor.Components[j];
                if (cur > component.LastExecuteTime + component.TimeInterval)
                {
                    component.LastExecuteTime = cur;
                    tasks.Add(component.Update(Time.CurrentMs));
                }
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        tasks.Clear();
        ObjectPool<List<Task>>.Return(tasks);
    }
}