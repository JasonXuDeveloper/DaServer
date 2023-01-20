using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;
using Nino.Serialization;

namespace DaServer.Shared.Core;

/// <summary>
/// A communication between the client and the server.
/// </summary>
[NinoSerialize()]
public struct RemoteCall
{
    [NinoMember(0)] public int MsgId;
    [NinoMember(1)] public int RequestId;
    [NinoMember(2)] internal byte[]? MessageData;

    public IMessage? MessageObj;
}