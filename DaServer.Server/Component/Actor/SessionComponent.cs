using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Core;

namespace DaServer.Server.Component;

/// <summary>
/// 处理会话的组件
/// </summary>
public class SessionComponent: ActorComponent
{
    /// <summary>
    /// 确保不会被删除该组件
    /// </summary>
    public override ComponentRole Role => ComponentRole.LowLevel;

    /// <summary>
    /// 1000 ms 执行一次会话脚本
    /// </summary>
    public override int TimeInterval => 1000;

    public void ChangeSession(Session newSession)
    {
        // 老Session下线
        Actor.Session.Dispose();
        // 新Session上线
        Actor.Session = newSession;
    }
    
    public override Task Create()
    {
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        Actor.Session.End();
        return Task.CompletedTask;
    }

    public override Task Update(long currentMs)
    {
        if (!Actor.Session.Connected)
        {
            Actor.Destroy();
        }
        return Task.CompletedTask;
    }
}