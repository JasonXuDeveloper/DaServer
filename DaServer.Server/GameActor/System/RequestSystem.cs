using System.Threading.Tasks;
using DaServer.Server.Core;

namespace DaServer.Server.GameActor;

public class RequestSystem: ActorSystem<RequestComponent>
{
    public override Task Update(long currentMs)
    {
        return base.Update(currentMs);
    }
}