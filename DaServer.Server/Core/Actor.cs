using System.Threading;
using System.Threading.Tasks;
using DaServer.Server.GameActor;
using DaServer.Server.Component;
using DaServer.Server.Extension;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using Destructurama.Attributed;

namespace DaServer.Server.Core;

public class Actor
{
    /// <summary>
    /// Actor ID初始值
    /// </summary>
    private static long _id = 10000;

    /// <summary>
    /// 实体ID
    /// </summary>
    public long Id { get; } = Interlocked.Increment(ref _id);
    
    /// <summary>
    /// 持有Actor的游戏系统
    /// </summary>
    public Entity OwnerEntity { get; }

    /// <summary>
    /// Actor系统
    /// </summary>
    [NotLogged]
    public ActorSystemComponent ActorSystemComponent => OwnerEntity.GetComponent<ActorSystemComponent>()!;
    
    /// <summary>
    /// Actor的会话
    /// </summary>
    public Session Session { get; set; }
    
    /// <summary>
    /// Start ms - 开始 ms
    /// </summary>
    public long StartMs { get; } = Time.CurrentMs;
    
    /// <summary>
    /// Online ms - 在线 ms
    /// </summary>
    public long OnlineMs => Time.CurrentMs - StartMs;
    
    public Actor(Entity ownerEntity, Session session)
    {
        OwnerEntity = ownerEntity;
        Session = session;
        //添加必备组件
        _ = AddComponent<SessionComponent, SessionSystem>();
        _ = AddComponent<RequestComponent, RequestSystem>();
    }
    
    /// <summary>
    /// 销毁Actor
    /// </summary>
    public void Destroy()
    {
        //释放会话
        Session.Dispose();
        //删除Actor引用
        ActorSystemComponent.RemoveActor(this);
    }
    
    /// <summary>
    /// 发送个需要被客户端派发的方法
    /// </summary>
    /// <param name="val"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public Task Send<T>(T? val) where T : IMessage => Respond(0, val);

    /// <summary>
    /// 回复某个请求
    /// </summary>
    /// <param name="requestId"></param>
    /// <param name="val"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task Respond<T>(int requestId, T? val) where T : IMessage
    {
        //如果是空，则请求出了问题，抛异常
        if (val is null)
        {
            // 发送错误
            await Session.SendAsync(MessageFactory.ToRemoteCallMessage(requestId, MError.Empty));
            return;
        }

        // 发送任务
        var buf = MessageFactory.ToRemoteCallMessage(requestId, val);
        await Session.SendAsync(buf);
    }

    public TActorComponent AddComponent<TActorComponent, TActorSystem>()
        where TActorComponent : ActorComponent<TActorSystem>
        where TActorSystem : ComponentHolder, IActorSystem<ActorComponent<TActorSystem>>, new() =>
        (TActorComponent)ActorSystemComponent.GetSystem<TActorSystem>().AddComponent(this);

    public TActorComponent? GetComponent<TActorComponent, TActorSystem>()
        where TActorComponent : ActorComponent<TActorSystem>
        where TActorSystem : ComponentHolder, IActorSystem<ActorComponent<TActorSystem>>, new() =>
        (TActorComponent?)ActorSystemComponent.GetSystem<TActorSystem>().GetComponent(this);
    
    public void RemoveComponent<TActorSystem>()
        where TActorSystem : ComponentHolder, IActorSystem<ActorComponent<TActorSystem>>, new() =>
        ActorSystemComponent.GetSystem<TActorSystem>().RemoveComponent(this);
    
    public void RemoveAllComponents<TActorSystem>()
        where TActorSystem : ComponentHolder, IActorSystem<ActorComponent<TActorSystem>>, new() =>
        ActorSystemComponent.RemoveAllComponents(this);

    public TActorSystem GetSystem<TActorSystem>()
        where TActorSystem : ComponentHolder, IActorSystem, new()
        => ActorSystemComponent.GetSystem<TActorSystem>();
}