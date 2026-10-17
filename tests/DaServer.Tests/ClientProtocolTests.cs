using DaServer.Net;

namespace DaServer.Tests;

/// <summary>A client connection starts with the client's hello: another protocol version is told why and disconnected.</summary>
public class ClientProtocolTests : ClusterTest
{
    private sealed class Recorder : IConnectionHandler
    {
        public readonly TaskCompletionSource<(RpcStatus Status, string? Text)> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Frames;

        public void OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame)
        {
            Interlocked.Increment(ref Frames);
            var payload = FrameHeader.PayloadOf(in header, frame);
            Reply.TrySetResult((header.Status, payload.IsEmpty ? null : Nino.Core.NinoDeserializer.Deserialize<string>(payload)));
        }

        public void OnClosed(Connection connection, Exception? error) => Closed.TrySetResult();

        public Connection? Connection;
    }

    private async Task<Recorder> Send(Node gateway, FrameKind kind, uint op, long key)
    {
        var recorder = new Recorder();
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
        var recorder = await Send(gateway, FrameKind.Control, ControlOp.ClientHello, ControlOp.ClientVersion + 1);
        var (status, text) = await recorder.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(status, Is.EqualTo(RpcStatus.Error));
        Assert.That(text, Does.Contain($"Client protocol {ControlOp.ClientVersion + 1}").And.Contain($"speaks {ControlOp.ClientVersion}"));

        // It is not a member of ClientGroup.All meanwhile: a broadcast it would misread does not reach it.
        int frames = recorder.Frames;
        gateway.Send(ClientGroup.All, new Chat(default, "all"));
        await Task.Delay(200);
        Assert.That(recorder.Frames, Is.EqualTo(frames));

        // Whatever it sends next, another hello included, closes the connection.
        using (var again = FrameWriter.Rent())
        {
            again.SetCallId(2);
            again.SetHeader(FrameKind.Control, 0, ControlOp.ClientHello, ControlOp.ClientVersion + 1);
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
}
