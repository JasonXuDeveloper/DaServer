using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Core;
using Nino.Shared.IO;

namespace DaServer.Server.Component;

/// <summary>
/// Actor系统组件
/// </summary>
public class ActorSystemComponent: Shared.Core.Component
{
    public override ComponentRole Role => ComponentRole.LowLevel;
    
    /// <summary>
    /// session - actor 索引
    /// </summary>
    internal ConcurrentDictionary<Session, Actor> Actors  = null!;
    
    /// <summary>
    /// (long) id - actor 索引
    /// </summary>
    internal ConcurrentDictionary<long, Actor> ActorsFromId = null!;

    /// <summary>
    /// actor列表
    /// </summary>
    public List<Actor> ActorList { get; private set; } = null!;
    
    public override Task Create()
    {
        Actors = new ConcurrentDictionary<Session, Actor>();
        ActorsFromId = new ConcurrentDictionary<long, Actor>();
        ActorList = new List<Actor>();
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        Actors.Clear();
        ActorsFromId.Clear();
        //循环每个Actor并调用Request
        int cnt = ActorList.Count;
        for (int i = 0; i < cnt; i++)
        {
            //按顺序处理每个actor的消息
            Actor actor = ActorList[i];
            actor.Destroy();
        }
        ActorList.Clear();
        return Task.CompletedTask;
    }
    
    public override async Task Update(long currentMs)
    {
        //循环每个Actor并调用Request
        int cnt = ActorList.Count;
        var tasks = ObjectPool<List<Task>>.Request();
        tasks.Clear();
        for (int i = 0; i < cnt; i++)
        {
            //按顺序处理每个actor的消息
            Actor actor = ActorList[i];
            int compCnt = actor.Components.Count;
            for (int j = 0; j < compCnt; j++)
            {
                if (j >= actor.Components.Count) break;
                var component = actor.Components[j];
                if (currentMs > component.LastExecuteTime + component.TimeInterval)
                {
                    component.LastExecuteTime = currentMs;
                    tasks.Add(component.Update(currentMs));
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