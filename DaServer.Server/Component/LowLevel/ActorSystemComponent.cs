using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Core;
using DaServer.Shared.Misc;
using Nito.AsyncEx;

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

    /// <summary>
    /// 执行Actor组件的线程
    /// </summary>
    private Thread _actorThread = null!;
    
    /// <summary>
    /// 执行Actor组件的线程的信号量
    /// </summary>
    private AutoResetEvent _resetEvent = null!;
    
    /// <summary>
    /// 待执行的组件队列
    /// </summary>
    private ConcurrentQueue<ActorComponent> _actorCompQueue = null!;

    public override Task Create()
    {
        Actors = new ConcurrentDictionary<Session, Actor>();
        ActorsFromId = new ConcurrentDictionary<long, Actor>();
        ActorList = new List<Actor>();
        _actorCompQueue = new();
        _resetEvent = new (false);
        _actorThread = new Thread(() =>
        {
            async Task ProcessActorComponents()
            {
                Logger.Info("Start ActorComponents Process on Thread {i}", Thread.CurrentThread.ManagedThreadId);
                while (true)
                {
                    _resetEvent.WaitOne();
                    var currentMs = Time.CurrentMs;
                    while (_actorCompQueue.TryDequeue(out var comp))
                    {
                        await comp.Update(currentMs);
                    }
                }
            }

            AsyncContext.Run(ProcessActorComponents);
        });
        _actorThread.Name = "ActorThread";
        _actorThread.UnsafeStart();
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
    
    public override Task Update(long currentMs)
    {
        //循环每个Actor并调用Request
        int cnt = ActorList.Count;
        bool flag = false;
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
                    _actorCompQueue.Enqueue((ActorComponent)component);
                    flag = true;
                }
            }
        }

        if (flag)
        {
            _resetEvent.Set();
        }
        
        return Task.CompletedTask;
    }
}