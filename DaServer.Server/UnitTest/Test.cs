using System.Threading;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Server.Request;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Server.UnitTest;

public class Test : Request<Actor, MTestRequest, MTestResponse>
{
    public override Task<MTestResponse> OnRequest(Actor actor, MTestRequest request)
    {
        Logger.Info("MTestRequest.OnRequest: actor {@actor}, request {@request} at thread {t}", actor, request,
            Thread.CurrentThread.ManagedThreadId);
        return Task.FromResult(new MTestResponse
        {
            Txt = "response"
        });
    }
}