using Nino.Serialization;

namespace DaServer.Shared.Message;

[NinoSerialize()]
[Message(MsgId.Void)]
public struct MVoid : IMessage
{
    public static MVoid Empty => new MVoid();
}