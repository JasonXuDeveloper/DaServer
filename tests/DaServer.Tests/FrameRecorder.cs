using DaServer.Net;

namespace DaServer.Tests;

/// <summary>A channel handler for tests that drive a channel frame by frame: records what arrives, and the close.</summary>
internal sealed class FrameRecorder : IChannelHandler
{
    /// <summary>Frames received (any thread).</summary>
    public int Frames;

    /// <summary>The statuses of the replies received, in order.</summary>
    public readonly ConcurrentQueue<RpcStatus> Statuses = new();

    /// <summary>The first frame's status, and its payload read as a string (null when empty).</summary>
    public readonly TaskCompletionSource<(RpcStatus Status, string? Text)> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The connection this handler serves, for a test that sends on it.</summary>
    public Connection? Connection;

    public void OnFrame(IFrameChannel channel, in FrameHeader header, ReadOnlySpan<byte> frame)
    {
        Interlocked.Increment(ref Frames);
        if (header.Kind == FrameKind.Reply) Statuses.Enqueue(header.Status);
        if (Reply.Task.IsCompleted) return;
        var payload = FrameHeader.PayloadOf(in header, frame);
        Reply.TrySetResult((header.Status, payload.IsEmpty ? null : Nino.Core.NinoDeserializer.Deserialize<string>(payload)));
    }

    public void OnClosed(IFrameChannel channel, Exception? error) => Closed.TrySetResult();
}
