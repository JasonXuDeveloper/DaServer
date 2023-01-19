using System.Threading.Tasks;
using DaServer.Shared.Core;

namespace DaServer.Shared.Component;

public class NetComponent: Core.Component
{
    internal override ComponentRole Role => ComponentRole.LowLevel;
    
    public override Task Create()
    {
        throw new System.NotImplementedException();
    }

    public override Task Destroy()
    {
        throw new System.NotImplementedException();
    }

    public override Task Update(int currentTick)
    {
        throw new System.NotImplementedException();
    }
}