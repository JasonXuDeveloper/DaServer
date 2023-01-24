using System.Threading.Tasks;
using DaServer.Shared.Core;

namespace DaServer.Server.Core;

public abstract class ActorComponent: Shared.Core.Component
{
    public override ComponentRole Role => ComponentRole.HighLevel;

    public Actor Actor => (Actor)Holder;

    public abstract override Task Create();

    public abstract override Task Destroy();

    public abstract override Task Update(long currentMs);
}