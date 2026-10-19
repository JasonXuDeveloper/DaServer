using System.Net.WebSockets;
using DaServer.Net;

namespace DaServer.Tests;

/// <summary>Frames over WebSockets (<see cref="WebSocketChannel"/>) against a scripted socket.</summary>
public class WebSocketChannelTests
{
    /// <summary>Hands out the scripted messages, one per data read; records the size of every read's buffer.</summary>
    private sealed class ScriptedSocket(params byte[][] messages) : WebSocket
    {
        private readonly Queue<byte[]> _messages = new(messages);
        private readonly TaskCompletionSource _silent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<int> Reads = [];
        public readonly TaskCompletionSource Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            lock (Reads) Reads.Add(buffer.Length);
            if (_messages.Count == 0)
            {
                Idle.TrySetResult();
                await _silent.Task; // the peer stays silent
            }

            if (buffer.IsEmpty) return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Binary, false); // data is ready
            var message = _messages.Dequeue();
            message.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(message.Length, WebSocketMessageType.Binary, true);
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => default;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => _silent.TrySetException(new WebSocketException("aborted"));
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static byte[] Ping()
    {
        using var w = FrameWriter.Rent();
        w.SetHeader(FrameKind.Control, 0, ControlOp.Ping, 0);
        return w.Frame.ToArray();
    }

    [Test]
    public async Task ASilentPeerHoldsNoReceiveBuffer([Values] bool releaseIdle)
    {
        // A server's crowd of idle browsers: between messages the channel waits with a zero-byte read, renting its 16 KB
        // buffer only for a message. Without releaseIdle (clients) it keeps one buffer and reads into it.
        var socket = new ScriptedSocket(Ping(), Ping());
        var handler = new FrameRecorder();
        var channel = new WebSocketChannel(socket, handler, FrameHeader.MaxFrameSize, 1 << 20, releaseIdle);
        var running = channel.RunAsync();
        await socket.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(handler.Frames, Is.EqualTo(2));
        int[] reads;
        lock (socket.Reads) reads = [.. socket.Reads];
        Assert.That(reads, releaseIdle
            ? Is.EqualTo(new[] { 0, 16 * 1024, 0, 16 * 1024, 0 })
            : Is.EqualTo(new[] { 16 * 1024, 16 * 1024, 16 * 1024 }));
        channel.Close();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
