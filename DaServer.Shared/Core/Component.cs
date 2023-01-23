using System;
using System.Threading.Tasks;

namespace DaServer.Shared.Core;

/// <summary>
/// Component Role - 组件角色
/// </summary>
public enum ComponentRole: byte
{
    LowLevel = 0,
    HighLevel = 1,
}
public abstract class Component
{
    protected Component()
    {
        if (System == null)
        {
            throw new InvalidOperationException(
                $"Can not create instance of {GetType()} with `new()`, use `AddComponent()` instead. " +
                $"无法使用`new()`创建{GetType()}的实例，请使用`AddComponent()`代替。");
        }
    }

    public System System { get; internal set; }

    public virtual ComponentRole Role => ComponentRole.HighLevel;
    public virtual int TickInterval => 1;
    public abstract Task Create();
    public abstract Task Destroy();
    public abstract Task Update(int currentTick);
}