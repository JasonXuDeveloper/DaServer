using Nino.Serialization;

namespace DaServer.Shared.Message;

[NinoSerialize()]
[Message(MsgId.Error)]
public struct MError : IMessage
{
    public static MError Empty => new MError();
}