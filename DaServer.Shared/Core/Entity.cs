using System.Threading;
using DaServer.Shared.Misc;
using Timer = System.Timers.Timer;

namespace DaServer.Shared.Core;

/// <summary>
/// Entity - 实体
/// </summary>
public sealed class Entity: ComponentHolder
{
    /// <summary>
    /// 实体ID初始值
    /// </summary>
    private static long _id = 0;

    /// <summary>
    /// 实体ID
    /// </summary>
    public long Id { get; } = Interlocked.Increment(ref _id);

    /// <summary>
    /// Constructor - 构造函数
    /// </summary>
    public Entity()
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
    private long StartMs { get; } = Time.CurrentMs;

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
    private async void Update()
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
    }
}