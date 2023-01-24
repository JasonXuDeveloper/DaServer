using DaServer.Shared.Misc;
using Nito.AsyncEx;
using Timer = System.Timers.Timer;

namespace DaServer.Shared.Core;

/// <summary>
/// System - 系统
/// </summary>
public sealed class System: ComponentHolder
{
    /// <summary>
    /// Constructor - 构造函数
    /// </summary>
    public System()
    {
        // Add timer
        _timer = new Timer(10);
        _timer.Elapsed += (_, _) => Update();
        _timer.AutoReset = true;
        _timer.Enabled = true;
    }

    /// <summary>
    /// Timer - 计时器
    /// </summary>
    private readonly Timer _timer;
    
    /// <summary>
    /// Start Ms - 开始 Ms
    /// </summary>
    private readonly long _startMs = Time.CurrentMs;

    /// <summary>
    /// Enable the system - 启用系统
    /// </summary>
    public void Enable()
    {
        _timer.Enabled = true;
    }
    
    /// <summary>
    /// Disable the system - 禁用系统
    /// </summary>
    public void Disable()
    {
        _timer.Enabled = false;
    }
    
    /// <summary>
    /// Update all components - 更新所有组件
    /// </summary>
    private void Update()
    {
        //全部组件在同一个线程执行即可
        using (var ctx = new AsyncContext())
        {
            ctx.SynchronizationContext.OperationStarted();
            //派发异步任务
            ctx.SynchronizationContext.Post(async _ =>
            {
                var cur = Time.CurrentMs;
                int cnt = Components.Count;
                for (int i = 0; i < cnt; i++)
                {
                    if (i >= Components.Count) break;
                    var component = Components[i];
                    if (cur > component.LastExecuteTime + component.TimeInterval)
                    {
                        component.LastExecuteTime = cur;
                        await component.Update(cur).ConfigureAwait(false);
                    }
                }
                
                ctx.SynchronizationContext.OperationCompleted();
            }, null);
            //执行，在被通知前不会退出
            ctx.Execute();
        }
    }
}