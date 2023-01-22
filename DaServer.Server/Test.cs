using System;
using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using DaServer.Shared.Request;
using Nino.Serialization;

namespace DaServer;

public class Test : Request<Actor, MTestRequest, MTestResponse>
{
    public override Task<MTestResponse> OnRequest(Actor actor, MTestRequest request)
    {
        Logger.Info("服务端收到请求");
        Logger.Info("session.id {Session}", actor.Session.Id);
        Logger.Info("request.txt {request}", request.Txt);
        return Task.FromResult(new MTestResponse()
        {
            Txt = "response"
        });
    }
}