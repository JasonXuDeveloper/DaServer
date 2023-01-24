using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Server.Component;
using DaServer.Shared.Core;

namespace DaServer.Server.Core;

public abstract class ActorComponent: Shared.Core.Component
{
    /// <summary>
    /// 组件级别
    /// </summary>
    public override ComponentRole Role => ComponentRole.HighLevel;

    /// <summary>
    /// 持有该组件的Actor
    /// </summary>
    public Actor Actor => (Actor)Owner;

    /// <summary>
    /// 全部Actor列表
    /// </summary>
    public List<Actor> ActorList => Actor.ActorSystem.ActorList;

    public abstract override Task Create();

    public abstract override Task Destroy();

    public abstract override Task Update(long currentMs);
}