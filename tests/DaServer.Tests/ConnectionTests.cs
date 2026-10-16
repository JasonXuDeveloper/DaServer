using System.Net;
using System.Net.Sockets;
using DaServer.Net;

namespace DaServer.Tests;

/// <summary>A connection's send side: queued frames reach the peer whole and in order, written at once or on a flusher's beat.</summary>
public class ConnectionTests
{
    private sealed class NoFrames : IConnectionHandler
    {
        public void OnFrame(Connection connection, in FrameHeader header, ReadOnlySpan<byte> frame) { }
        public void OnClosed(Connection connection, Exception? error) { }
    }

    /// <summary>A sender connected to a raw socket the test reads (or does not read yet).</summary>
    private static async Task<(Connection Sender, Socket Reader, TcpListener Listener)> Pair(ConnectionOptions options)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var sender = await Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new NoFrames(), options);
        return (sender, await accept, listener);
    }

    private static byte[] Frame(int thread, int sequence, int payload)
    {
        using var w = FrameWriter.Rent(payload + FrameHeader.Size);
        w.WriteRaw(new byte[payload]);
        w.SetHeader(FrameKind.Message, (uint)thread, 1, sequence);
        return w.Frame.ToArray();
    }

    [TestCase(false), TestCase(true), CancelAfter(30_000)]
    public async Task ASenderOutrunningItsReaderLosesNothingAndKeepsOrder(bool flushed, CancellationToken cancellationToken)
    {
        // 4 threads send 8 MB in all while the peer reads nothing: the socket's buffers fill (flushed: a beat's write
        // hits WouldBlock and the carry loop takes over the writer). Then the peer reads: every frame arrives, each
        // thread's in order.
        const int threads = 4, frames = 500, payload = 4096;
        using var flusher = flushed ? new SendFlusher(TimeSpan.FromMilliseconds(2)) : null;
        var (sender, reader, listener) = await Pair(new ConnectionOptions { MaxPendingSendBytes = 64 * 1024 * 1024, Flusher = flusher });
        using var _ = listener.Server;
        using var __ = reader;
        var sending = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < frames; i++)
                Assert.That(sender.Send(Frame(t, i, payload)), Is.True);
        })).ToArray();
        await Task.WhenAll(sending);
        await Task.Delay(500, cancellationToken); // the socket stays full meanwhile

        var next = new int[threads];
        var buffer = new byte[256 * 1024];
        int filled = 0, received = 0;
        while (received < threads * frames)
        {
            filled += await reader.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, cancellationToken);
            int offset = 0;
            while (filled - offset >= FrameHeader.Size)
            {
                var header = FrameHeader.Read(buffer.AsSpan(offset));
                if (header.Length > filled - offset) break;
                Assert.That(header.Key, Is.EqualTo(next[header.Target]++), $"thread {header.Target}'s frames in order");
                offset += header.Length;
                received++;
            }

            Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
            filled -= offset;
        }

        Assert.That(next, Is.All.EqualTo(frames));
        sender.Close();
    }

    [Test, CancelAfter(10_000)]
    public async Task AFlushThatMeetsAClosedSocketDoesNotThrow(CancellationToken cancellationToken)
    {
        var (sender, reader, listener) = await Pair(new ConnectionOptions());
        using var _ = listener.Server;
        reader.Close(); // the peer goes away
        for (int i = 0; i < 200 && !sender.IsClosed; i++)
        {
            sender.Send(Frame(0, i, 1024)); // never throws, whatever the socket says
            await Task.Delay(5, cancellationToken);
        }

        Assert.That(sender.IsClosed, Is.True, "the reset closed the connection");
    }
}
