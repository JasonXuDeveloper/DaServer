using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Request;

namespace DaServer;

public struct TestRequest : IMessage
{
    
}

public struct TestResponse : IMessage
{
    
}
public class Test: Request<Actor,TestRequest, MVoid>
{
    public override Task<MVoid> OnRequest(Actor actor, TestRequest request)
    {
        return Task.FromResult(MVoid.Empty);
    }
}