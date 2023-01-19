using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Core;

namespace DaServer.Shared.Component;

public class ActorComponent: Core.Component
{
    internal override ComponentRole Role => ComponentRole.LowLevel;
    
    private ConcurrentDictionary<Session, Actor> _actors = new ConcurrentDictionary<Session, Actor>();

    public override Task Create()
    {
        _actors = new ConcurrentDictionary<Session, Actor>();
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        _actors.Clear();
        return Task.CompletedTask;
    }
    
    public Actor AddActor(Session session)
    {
        Actor actor = new Actor(session);
        _actors.TryAdd(session, actor);
        return actor;
    }
    
    public void RemoveActor(Session session)
    {
        _actors.TryRemove(session, out _);
    }

    public void RemoveActor(Actor actor)
    {
        foreach (var pair in _actors)
        {
            if (pair.Value == actor)
            {
                _actors.TryRemove(pair.Key, out _);
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
        foreach (var actor in _actors.Values)
        {
            while (actor.Requests.TryDequeue(out var request))
            {
                var respond = await request.requestTask;
                await actor.Session.Send(request.requestId, respond);
            }
        }
        //TODO 检测actor的session断开
    }
}