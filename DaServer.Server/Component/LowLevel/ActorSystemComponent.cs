using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Core;
using DaServer.Shared.Interface;
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
    /// 全部ActorSystem
    /// ActorSystem是泛型的，所以这边存它的基类（ComponentHolder, IUpdatable, IActorSystem)
    /// </summary>
    private List<ComponentHolder> _actorSystems = new();
    
    /// <summary>
    /// 全部ActorSystem
    /// </summary>
    private ConcurrentDictionary<Type, ComponentHolder> _actorSystemDict = new();

    public void RemoveAllComponents(Actor actor)
    {
        for (int i = 0; i < _actorSystems.Count; i++)
        {
            var actorSystem = _actorSystems[i];
            ((IActorSystem)actorSystem).RemoveComponent(actor);
        }
    }
    
    public TActorSystem GetSystem<TActorSystem>() where TActorSystem : ComponentHolder, IActorSystem, new()
    {
        if(!_actorSystemDict.TryGetValue(typeof(TActorSystem), out var actorSystem))
        {
            actorSystem = new TActorSystem();
            _actorSystems.Add(actorSystem);
            _actorSystemDict.TryAdd(typeof(TActorSystem), actorSystem);
            Logger.Info("Created ActorSystem: {sys}", typeof(TActorSystem));
        }

        return (TActorSystem)actorSystem;
    }

    public override Task Create()
    {
        Actors = new ConcurrentDictionary<Session, Actor>();
        ActorsFromId = new ConcurrentDictionary<long, Actor>();
        ActorList = new List<Actor>();
        _actorSystems = new();
        _actorSystemDict = new();
        _resetEvent = new (false);
        _actorThread = new Thread(() =>
        {
            async Task ProcessActorComponents()
            {
                Logger.Info("Started ActorSystem Processor on Thread {i}", Thread.CurrentThread.ManagedThreadId);
                while (true)
                {
                    _resetEvent.WaitOne();
                    var currentMs = Time.CurrentMs;
                    for(int i=0; i< _actorSystems.Count; i++)
                    {
                        if(i >= _actorSystems.Count) break;
                        var actorSystem = _actorSystems[i];
                        if (actorSystem is IUpdatable updatable)
                        {
                            await updatable.Update(currentMs);
                        }
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
        cnt = _actorSystems.Count;
        for (int i = 0; i < cnt; i++)
        {
            var actorSystem = _actorSystems[i];
            actorSystem.RemoveAllComponents();
        }
        _actorSystems.Clear();
        return Task.CompletedTask;
    }
    
    public override Task Update(long currentMs)
    {
        //通知Actor线程更新
        _resetEvent.Set();
        return Task.CompletedTask;
    }
}