using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using DaServer.Shared.Request;
using Nino.Serialization;

namespace DaServer;

[Message(100_1)]
[NinoSerialize()]
public struct MTestRequest : IMessage
{
    [NinoMember(1)] public string Txt;
}

[Message(100_2)]
[NinoSerialize()]
public struct MTestResponse : IMessage
{
    [NinoMember(1)] public string Txt;
}

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