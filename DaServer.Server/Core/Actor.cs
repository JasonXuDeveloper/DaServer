using System.Threading.Tasks;
using DaServer.Server.Component;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Server.Core;

public class Actor: ComponentHolder
{
    /// <summary>
    /// 持有Actor的游戏系统
    /// </summary>
    public Shared.Core.System System { get; }
    
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
    
    public Actor(Shared.Core.System system, Session session)
    {
        System = system;
        Session = session;
        //添加必备组件
        AddComponent<SessionComponent>();
        AddComponent<RequestComponent>();
    }
    
    /// <summary>
    /// 销毁Actor
    /// </summary>
    public void Destroy()
    {
        //释放会话
        Session.Dispose();
        //删除组件
        RemoveAllComponents();
        //删除Actor引用
        System.GetComponent<ActorProcessComponent>()!.RemoveActor(this);
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
            await Session.SendAsync(MessageFactory.GetMessage(requestId, MError.Empty));
            return;
        }

        // 发送任务
        var buf = MessageFactory.GetMessage(requestId, val);
        await Session.SendAsync(buf);
    }
}