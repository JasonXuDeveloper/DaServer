using DaServer.Net;

namespace DaServer.Tests;

/// <summary>A client connection starts with the client's hello: another protocol version is told why and disconnected.</summary>
public class ClientProtocolTests : ClusterTest
{
    [Test]
    public async Task ResumesAndTicketsDrawOnTheSessionsRate()
    {
        // Each costs the node a signature: a client looping them is limited like one looping messages.
        var gateway = await Start("a", gateway: true, clients: static o => (o.MessagesPerSecond, o.Burst) = (1, 2));
        var client = await Send(gateway, FrameKind.Control, ControlOp.ClientHello, ControlOp.Client.Packed);
        await client.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (uint call = 2; call < 6; call++)
        {
            using var w = FrameWriter.Rent();
            w.WriteRaw(new byte[16]); // not a ticket
            w.SetCallId(call);
            w.SetHeader(FrameKind.Control, 0, ControlOp.ResumeSession, 0);
            client.Connection!.Send(w.Frame);
        }

        await Eventually(() => client.Statuses.Count >= 5, "every resume answered");
        Assert.That(client.Statuses, Has.Some.EqualTo(RpcStatus.RateLimited), string.Join(", ", client.Statuses));
    }

    private async Task<FrameRecorder> Send(Node gateway, FrameKind kind, uint op, long key)
    {
        var recorder = new FrameRecorder();
        var connection = recorder.Connection = await Connection.ConnectAsync("127.0.0.1", gateway.ClientPort(), recorder, ConnectionOptions.ForClients());
        using var w = FrameWriter.Rent();
        w.SetCallId(1);
        w.SetHeader(kind, 0, op, key);
        connection.Send(w.Frame);
        return recorder;
    }

    [Test]
    public async Task AClientOfAnotherVersionIsToldBothVersionsAndCannotGoOn()
    {
        var gateway = await Start("a", gateway: true);
        var recorder = await Send(gateway, FrameKind.Control, ControlOp.ClientHello, new ProtocolLevels(ControlOp.Client.Max + 1, ControlOp.Client.Max + 1).Packed);
        var (status, text) = await recorder.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(status, Is.EqualTo(RpcStatus.Error));
        Assert.That(text, Does.Contain($"Client protocol {ControlOp.Client.Max + 1}").And.Contain($"speaks {ControlOp.Client}"));

        // It is not a member of ClientGroup.All meanwhile: a broadcast it would misread does not reach it.
        int frames = recorder.Frames;
        gateway.Send(ClientGroup.All, new Chat(default, "all"));
        await Task.Delay(200);
        Assert.That(recorder.Frames, Is.EqualTo(frames));

        // Whatever it sends next, another hello included, closes the connection.
        using (var again = FrameWriter.Rent())
        {
            again.SetCallId(2);
            again.SetHeader(FrameKind.Control, 0, ControlOp.ClientHello, new ProtocolLevels(ControlOp.Client.Max + 1, ControlOp.Client.Max + 1).Packed);
            recorder.Connection!.Send(again.Frame);
        }

        await recorder.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task AConnectionThatSkipsTheHelloIsClosed()
    {
        var gateway = await Start("a", gateway: true);
        var recorder = await Send(gateway, FrameKind.Message, TypeId<EchoText>.Value, 0);
        await recorder.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(recorder.Reply.Task.IsCompleted, Is.False, "nothing it sent ran");
    }

    /// <summary>
    /// Frames a hostile client crafts past the size check: flags naming trailers the frame does not hold, an extension
    /// block longer than the frame, an extension entry running past its block, a payload that is not the message. A frame
    /// whose layout does not hold closes that client's connection; a message that does not read is refused as malformed
    /// (the node reads no extension entry of a client's frame). The node goes on serving everyone else.
    /// </summary>
    [TestCase(FrameFlags.HasCall | FrameFlags.HasOrigin | FrameFlags.HasChain, 0, true, TestName = "Trailers longer than the frame")]
    [TestCase(FrameFlags.HasExtensions, 60_000, true, TestName = "An extension block longer than the frame")]
    [TestCase(FrameFlags.HasExtensions, 6, false, TestName = "An extension entry running past its block")]
    [TestCase((byte)0, -1, false, TestName = "A payload that is not the message")]
    public async Task HostileFramesHurtOnlyTheirConnection(byte flags, int blockSize, bool closes)
    {
        var gateway = await Start("a", gateway: true);
        var hostile = await Send(gateway, FrameKind.Control, ControlOp.ClientHello, ControlOp.Client.Packed);
        Assert.That((await hostile.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5))).Status, Is.EqualTo(RpcStatus.Ok), "introduced");

        // A [FromClient] message (Login: an 8-byte player id), crafted byte by byte; 3 bytes cannot be one.
        var frame = new byte[FrameHeader.Size + (blockSize < 0 ? 3 : 8)];
        new FrameHeader { Length = frame.Length, Kind = FrameKind.Message, Flags = flags, MessageId = TypeId<Login>.Value }.Write(frame);
        if (blockSize > 0) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), (ushort)blockSize);
        if (blockSize == 6) (frame[^6], frame[^5]) = ((byte)1, (byte)200); // one entry claiming 200 bytes in a 6-byte block
        hostile.Connection!.Send(frame);
        await Task.Delay(300);
        Assert.That(hostile.Closed.Task.IsCompleted, Is.EqualTo(closes), closes ? "a layout that does not hold closes the connection" : "a message that does not read is refused, the connection stays");

        // Whatever happened to that connection, the node serves a well-behaved client as before.
        await using var client = new ClusterClient();
        await client.ConnectAsync("127.0.0.1", gateway.ClientPort());
        Assert.That(await client.Ask(new Login(new PlayerId(901))), Is.True);
        Assert.That(gateway.IsReady, Is.True);
    }
}
