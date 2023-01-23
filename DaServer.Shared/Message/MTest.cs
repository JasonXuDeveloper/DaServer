using Nino.Serialization;

namespace DaServer.Shared.Message;

[Message(MsgId.TestRequest)]
[NinoSerialize()]
public struct MTestRequest : IMessage
{
    [NinoMember(1)] public string Txt { get; set; }
}

[Message(MsgId.TestResponse)]
[NinoSerialize()]
public struct MTestResponse : IMessage
{
    [NinoMember(1)] public string Txt { get; set; }
}
