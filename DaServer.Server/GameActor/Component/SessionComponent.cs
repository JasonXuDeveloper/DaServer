using System.Threading;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Core;
using DaServer.Shared.Misc;

namespace DaServer.Server.GameActor;

/// <summary>
/// 处理会话的组件
/// </summary>
public class SessionComponent: ActorComponent<SessionSystem>
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
            Logger.Info("Actor {Id} offline at thread {t}", Actor.Id, Thread.CurrentThread.ManagedThreadId);
            Actor.Destroy();
        }
        return Task.CompletedTask;
    }
}