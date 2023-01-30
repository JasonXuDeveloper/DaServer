using System.Collections.ObjectModel;
using System.Threading.Tasks;
using DaServer.Shared.Core;

namespace DaServer.Server.Core;

public abstract class ActorComponent<TActorSystem> : Shared.Core.Component
    where TActorSystem : ComponentHolder, IActorSystem<ActorComponent<TActorSystem>>
{
    /// <summary>
    /// 组件级别
    /// </summary>
    public override ComponentRole Role => ComponentRole.HighLevel;

    /// <summary>
    /// 持有该组件的ActorSystem
    /// </summary>
    public TActorSystem ActorSystem => (TActorSystem)Owner;

    /// <summary>
    /// 持有该组件的Actor
    /// </summary>
    public Actor Actor => ActorSystem.GetActor(this)!;

    /// <summary>
    /// 全部Actor列表
    /// </summary>
    public ReadOnlyCollection<Actor> ActorList => Actor.ActorSystemComponent.ActorList.AsReadOnly();

    public abstract override Task Create();

    public abstract override Task Destroy();

    public abstract override Task Update(long currentMs);
}