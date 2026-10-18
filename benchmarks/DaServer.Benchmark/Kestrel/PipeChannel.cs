using System.Buffers;
using System.IO.Pipelines;
using DaServer.Buffers;
using DaServer.Net;
using DaServer.Protocol;

namespace DaServer.Benchmark.Kestrel;

/// <summary>
/// Frames over an <see cref="IDuplexPipe"/> (a Kestrel connection, <c>UseConnectionHandler</c>): the candidate for
/// replacing the client listener's own sockets (roadmap: "Evaluate Kestrel's raw TCP"). Sends go through the same
/// <see cref="SendQueue"/> as TCP and WebSocket channels, each batch written to the pipe and flushed; receives parse the
/// pipe's buffers in place, copying only a frame that spans two of Kestrel's blocks.
/// </summary>
internal sealed class PipeChannel(IDuplexPipe pipe, Action abort, IChannelHandler handler, int maxFrameSize, int maxPendingSendBytes)
    : FrameChannel(handler, new SendQueue(maxPendingSendBytes, 16 * 1024, releaseIdle: true, keepSpare: false))
{
    public async Task RunAsync()
    {
        var sending = SendLoop();
        Exception? error = null;
        try
        {
            await ReceiveLoop().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e;
        }

        Close(error);
        await sending.ConfigureAwait(false);
    }

    private protected override void Abort() => abort();

    private async Task SendLoop()
    {
        try
        {
            while (!IsClosed)
            {
                await Queue.Signal.WaitAsync().ConfigureAwait(false);
                while (!IsClosed && Queue.TryTake(out var batch, out int length))
                {
                    try
                    {
                        pipe.Output.Write(new ReadOnlySpan<byte>(batch, 0, length));
                    }
                    finally
                    {
                        Queue.Release(batch);
                    }

                    var flushed = await pipe.Output.FlushAsync().ConfigureAwait(false);
                    KeepAlive.Touch(ref _lastSentMs);
                    if (flushed.IsCompleted) return;
                }
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
        finally
        {
            Queue.Close();
        }
    }

    private async Task ReceiveLoop()
    {
        var input = pipe.Input;
        while (true)
        {
            var read = await input.ReadAsync().ConfigureAwait(false);
            var buffer = read.Buffer;
            if (!buffer.IsEmpty) KeepAlive.Touch(ref _lastReceivedMs);
            var consumed = Dispatch(ref buffer);
            input.AdvanceTo(consumed, buffer.End);
            if (read.IsCompleted) return;
        }
    }

    /// <summary>Hand every whole frame of <paramref name="buffer"/> to the handler; the position after the last one.</summary>
    private SequencePosition Dispatch(ref ReadOnlySequence<byte> buffer)
    {
        using var batch = BeginSendBatch(); // as on TCP: what these frames cause leaves in one write per connection
        Span<byte> head = stackalloc byte[FrameHeader.Size];
        while (true)
        {
            var span = buffer.FirstSpan;
            if (span.Length >= FrameHeader.Size)
            {
                if (FrameHeader.TryReadFrame(span, maxFrameSize, out var header))
                {
                    Handler.OnFrame(this, in header, span[..header.Length]);
                    buffer = buffer.Slice(header.Length);
                    continue;
                }

                if (header.Length == 0 || buffer.Length < header.Length) return buffer.Start;
                SpanningFrame(ref buffer, header.Length); // whole, across Kestrel's blocks
                continue;
            }

            if (buffer.Length < FrameHeader.Size) return buffer.Start;
            buffer.Slice(0, FrameHeader.Size).CopyTo(head);
            int length = FrameHeader.Read(head).Length;
            if (length < FrameHeader.Size || length > maxFrameSize) throw new InvalidOperationException("Invalid frame length " + length);
            if (buffer.Length < length) return buffer.Start;
            SpanningFrame(ref buffer, length);
        }
    }

    private void SpanningFrame(ref ReadOnlySequence<byte> buffer, int length)
    {
        var copy = BufferPool.Rent(length);
        try
        {
            buffer.Slice(0, length).CopyTo(copy);
            var frame = copy.AsSpan(0, length);
            var header = FrameHeader.Read(frame);
            Handler.OnFrame(this, in header, frame);
        }
        finally
        {
            BufferPool.Return(copy);
        }

        buffer = buffer.Slice(length);
    }
}
