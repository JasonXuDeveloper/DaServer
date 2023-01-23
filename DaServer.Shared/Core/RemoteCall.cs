using DaServer.Shared.Message;
using Nino.Serialization;

namespace DaServer.Shared.Core;

/// <summary>
/// A communication between the client and the server.
/// </summary>
[NinoSerialize()]
public struct RemoteCall
{
    [NinoMember(0)] public int MsgId { get; set; }
    [NinoMember(1)] public int RequestId { get; set; }
    [NinoMember(2)] internal byte[]? MessageData;

    public IMessage? MessageObj { get; set; }
}