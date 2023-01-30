using System.Threading;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Server.Request;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Server.UnitTest;

public class Test : Request<Actor, MTestRequest, MTestResponse>
{
    public override async Task<MTestResponse> OnRequest(Actor actor, MTestRequest request)
    {
        await Task.Yield();// attempt to switch thread
        Logger.Info("MTestRequest.OnRequest: actor {@actor}, request {@request} on thread {t}", actor, request,
            Thread.CurrentThread.ManagedThreadId);// should not be able to switch thread
        return new MTestResponse
        {
            Txt = "response"
        };
    }
}