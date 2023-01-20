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
    [NinoMember(1)] public int a;
}

[Message(100_2)]
[NinoSerialize()]
public struct MTestResponse : IMessage
{
    
}
public class Test: Request<Actor,MTestRequest, MTestResponse>
{
    public override Task<MTestResponse> OnRequest(Actor actor, MTestRequest request)
    {
        Logger.Info("{actor}", actor);
        Logger.Info("{Session}", actor.Session.Id);
        return Task.FromResult(new MTestResponse());
    }
}