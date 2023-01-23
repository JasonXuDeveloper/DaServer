using System.Threading.Tasks;
using DaServer.Server.Request;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Server.UnitTest;

public class Test : Request<Actor, MTestRequest, MTestResponse>
{
    public override Task<MTestResponse> OnRequest(Actor actor, MTestRequest request)
    {
        Logger.Info("服务端收到请求");
        Logger.Info("actor {@actor}", actor);
        Logger.Info("request {@request}", request);
        return Task.FromResult(new MTestResponse()
        {
            Txt = "response"
        });
    }
}