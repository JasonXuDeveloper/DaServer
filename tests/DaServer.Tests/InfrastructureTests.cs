using DaServer.Concurrency;

namespace DaServer.Tests;

/// <summary>Unit tests of the runtime building blocks.</summary>
public class InfrastructureTests
{
    [Test]
    public void MpmcQueueIsFifoAndBounded()
    {
        var q = new MpmcQueue<int>(4);
        Assert.That(q.Capacity, Is.EqualTo(4));
        for (int i = 0; i < 4; i++) Assert.That(q.TryEnqueue(i), Is.True);
        Assert.That(q.TryEnqueue(99), Is.False, "full");
        for (int i = 0; i < 4; i++)
        {
            Assert.That(q.TryDequeue(out int v), Is.True);
            Assert.That(v, Is.EqualTo(i));
        }

        Assert.That(q.TryDequeue(out _), Is.False, "empty");
    }

    [Test]
    public void MpmcQueueLosesNothingUnderContention()
    {
        var q = new MpmcQueue<long>(1024);
        const int producers = 4, perProducer = 100_000;
        var seen = new ConcurrentDictionary<long, bool>();
        long consumed = 0;
        var consumers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (Interlocked.Read(ref consumed) < producers * perProducer)
            {
                if (q.TryDequeue(out long v))
                {
                    Assert.That(seen.TryAdd(v, true), Is.True, "duplicate item");
                    Interlocked.Increment(ref consumed);
                }
            }
        })).ToArray();
        Parallel.For(0, producers, p =>
        {
            for (long i = 0; i < perProducer; i++)
                while (!q.TryEnqueue(p * (long)perProducer + i)) Thread.SpinWait(1);
        });
        Task.WaitAll(consumers);
        Assert.That(seen.Count, Is.EqualTo(producers * perProducer));
    }

    [Test]
    public void BufferPoolRoundsToBucketsAndReuses()
    {
        var a = BufferPool.Rent(300);
        Assert.That(a.Length, Is.EqualTo(512));
        BufferPool.Return(a);

        // Buckets are process-wide FIFO queues (other tests leave buffers in them): rent until `a` comes back.
        var rented = new System.Collections.Generic.List<byte[]>();
        byte[] b;
        do rented.Add(b = BufferPool.Rent(400));
        while (b != a && rented.Count < 100_000);
        foreach (var buffer in rented) BufferPool.Return(buffer);
        Assert.That(b, Is.SameAs(a), "returned buffer is reused");
        BufferPool.Return(new byte[1000]); // foreign size: ignored, no corruption
        Assert.That(BufferPool.Rent(10).Length, Is.EqualTo(BufferPool.MinSize));
    }

    [Test]
    public void FrameHeaderRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[FrameHeader.Size];
        var h = new FrameHeader
        {
            Length = 1234, Kind = FrameKind.Message, Status = RpcStatus.Forbidden, Hops = 2, Flags = FrameFlags.HasOrigin,
            Target = 0xDEADBEEF, MessageId = 0x12345678, Key = -42,
        };
        h.Write(buffer);
        var r = FrameHeader.Read(buffer);
        Assert.That(r.Length, Is.EqualTo(1234));
        Assert.That(r.Kind, Is.EqualTo(FrameKind.Message));
        Assert.That(r.Status, Is.EqualTo(RpcStatus.Forbidden));
        Assert.That(r.Hops, Is.EqualTo(2));
        Assert.That(r.Flags, Is.EqualTo(FrameFlags.HasOrigin));
        Assert.That(r.Target, Is.EqualTo(0xDEADBEEF));
        Assert.That(r.MessageId, Is.EqualTo(0x12345678));
        Assert.That(r.Key, Is.EqualTo(-42));
    }

    [Test]
    public void FrameWriterSerializesAndGrows()
    {
        using var w = FrameWriter.Rent(32);
        var nino = w.Nino;
        Nino.Core.NinoSerializer.Serialize(new string('x', 5000), ref nino);
        w.SetHeader(FrameKind.Message, 0, 7, 9);
        var header = FrameHeader.Read(w.Frame);
        Assert.That(header.Length, Is.EqualTo(w.Length));
        var reader = new Nino.Core.Reader(w.Payload);
        Nino.Core.NinoDeserializer.Deserialize(out string text, ref reader);
        Assert.That(text, Has.Length.EqualTo(5000));
    }

    [Test]
    public void TrailersCarryTheOriginAndAPerHopCallId()
    {
        using var w = FrameWriter.Rent();
        var nino = w.Nino;
        Nino.Core.NinoSerializer.Serialize(new Add(5), ref nino);
        int payload = w.Payload.Length;
        w.SetCallId(41);
        w.SetOrigin(new Origin(7, 8)); // after the call id: the call id moves behind it
        w.SetHeader(FrameKind.Message, 3, MessageType<Add>.Id, 9);
        var header = FrameHeader.Read(w.Frame);
        Assert.That(header.IsRequest, Is.True);
        Assert.That(header.Flags, Is.EqualTo(FrameFlags.HasCall | FrameFlags.HasOrigin));
        Assert.That(FrameHeader.CallIdOf(in header, w.Frame), Is.EqualTo(41));
        Assert.That(FrameHeader.OriginOf(in header, w.Frame), Is.EqualTo(new Origin(7, 8)));
        Assert.That(FrameHeader.PayloadOf(in header, w.Frame).Length, Is.EqualTo(payload), "the trailer is not payload");

        // A relaying hop copies the frame and rewrites the call id in place: the frame does not grow.
        using var relayed = FrameWriter.CopyOf(w.Frame);
        relayed.SetCallId(77);
        relayed.SetHeader(FrameKind.Message, 3, MessageType<Add>.Id, 9, hops: 1);
        Assert.That(relayed.Length, Is.EqualTo(w.Length));
        Assert.That(relayed.CallId, Is.EqualTo(77));
        Assert.That(relayed.Origin, Is.EqualTo(new Origin(7, 8)), "the origin travels on");
        Assert.That(relayed.Payload.SequenceEqual(w.Payload), Is.True);
        var reader = new Nino.Core.Reader(relayed.Payload);
        Nino.Core.NinoDeserializer.Deserialize(out Add add, ref reader);
        Assert.That(add.Delta, Is.EqualTo(5));
    }

    [Test]
    public void PendingCallTableCompletesTimesOutAndRejectsStaleIds()
    {
        var table = new PendingCallTable(4);
        var call = PendingCall<int>.Rent(null);
        var task = call.Task;
        Assert.That(table.TryRegister(call, timeoutMs: 0, out uint id), Is.True);

        var w = FrameWriter.Rent();
        var nino = w.Nino;
        Nino.Core.NinoSerializer.Serialize(41, ref nino);
        Assert.That(table.TryComplete(id, RpcStatus.Ok, w.Payload), Is.True);
        Assert.That(table.TryComplete(id, RpcStatus.Ok, w.Payload), Is.False, "completed once only");
        w.Dispose();
        Assert.That(task.AsTask().Result, Is.EqualTo(41));

        // Slot reuse: a late response for the old id must not complete the new call.
        var second = PendingCall<int>.Rent(null);
        Assert.That(table.TryRegister(second, timeoutMs: 1, out uint id2), Is.True);
        Assert.That(id2, Is.Not.EqualTo(id));
        Assert.That(table.Take(id), Is.Null);
        Thread.Sleep(5);
        Assert.That(table.SweepExpired(), Is.EqualTo(1));
        var timedOut = Assert.ThrowsAsync<RpcException>(async () => await second.Task);
        Assert.That(timedOut!.Status, Is.EqualTo(RpcStatus.Timeout));
        Assert.That(timedOut.Detail, Is.Null, "runtime failures are status-only");

        // A full table refuses instead of throwing (the caller fails the call as Overloaded).
        var held = PendingCall<int>.Rent(null);
        for (int i = 0; i < 16; i++) Assert.That(table.TryRegister(held, timeoutMs: 0, out _), Is.True);
        Assert.That(table.TryRegister(held, timeoutMs: 0, out _), Is.False);
    }

    [Test]
    public void ResendWindowsRecycleTheirOwnChunks()
    {
        // A busy link sends and gets acknowledged continuously: it reuses its released chunks, so it neither depends on
        // the shared pool's 64 KB bucket (which many backlogged links can empty) nor allocates.
        var window = new ResendWindow(32 << 20);
        var batch = new byte[ResendWindow.ChunkSize / 4];
        long sent = 0;
        Stream(); // warm: the window's first chunks come from the shared pool

        var held = new List<byte[]>();
        long before = BufferPool.AllocatedBytes;
        while (BufferPool.AllocatedBytes == before) held.Add(BufferPool.Rent(ResendWindow.ChunkSize)); // the bucket is empty
        long pool = BufferPool.AllocatedBytes, garbage = GC.GetAllocatedBytesForCurrentThread();
        Stream();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - garbage, rented = BufferPool.AllocatedBytes - pool;
        Assert.That(rented, Is.Zero, "no chunk rented from the shared pool");
        Assert.That(allocated, Is.Zero, "nothing allocated");
        foreach (var buffer in held) BufferPool.Return(buffer);

        void Stream()
        {
            for (int i = 0; i < 256; i++) // 4 MB through the window, acknowledged as it goes
            {
                window.OnSending(batch);
                window.Acknowledge(sent += batch.Length);
            }
        }
    }

    [Test]
    public void RendezvousPlacementIsStableAndMinimallyDisruptive()
    {
        ClusterMember Member(ulong id) => new()
        {
            NodeId = id, Status = MemberStatus.Up,
            Handlers = [new HostedMessage { MessageId = 7, HandlerTypeId = 70, Actor = true }],
        };

        var three = new ClusterView([Member(1), Member(2), Member(3)]);
        var four = new ClusterView([Member(1), Member(2), Member(3), Member(4)]);

        int moved = 0;
        for (long key = 0; key < 10_000; key++)
        {
            var before = three.HandlerTypeOwner(70, key)!.NodeId;
            var after = four.HandlerTypeOwner(70, key)!.NodeId;
            Assert.That(three.HandlerTypeOwner(70, key)!.NodeId, Is.EqualTo(before), "deterministic");
            if (before != after)
            {
                moved++;
                Assert.That(after, Is.EqualTo(4UL), "keys only move to the new node");
            }
        }

        Assert.That(moved, Is.InRange(2000, 3000), "about 1/4 of the keys move to the 4th node");
    }

    [Test]
    public void PulsesRoundTrip()
    {
        var pulse = new Pulse(0xDEADBEEF12345678, 7, 1L << 40, MemberStatus.Suspect, 123, 4567);
        Span<byte> wire = stackalloc byte[Pulse.Size];
        pulse.Write(wire);
        Assert.That(Pulse.Read(wire), Is.EqualTo(pulse));
    }

    [Test]
    public void MemberMergeOrder()
    {
        var a = new ClusterMember { Incarnation = 1, Heartbeat = 5, Status = MemberStatus.Up };
        Assert.That(new ClusterMember { Incarnation = 2, Heartbeat = 0, Status = MemberStatus.Up }.Supersedes(a), Is.True);
        Assert.That(new ClusterMember { Incarnation = 1, Heartbeat = 0, Status = MemberStatus.Dead }.Supersedes(a), Is.True);
        Assert.That(new ClusterMember { Incarnation = 1, Heartbeat = 6, Status = MemberStatus.Up }.Supersedes(a), Is.True);
        Assert.That(new ClusterMember { Incarnation = 1, Heartbeat = 4, Status = MemberStatus.Up }.Supersedes(a), Is.False);
    }

    [Test]
    public void MessageIdsAreTheHashOfTheTypeName()
    {
        // The analyzer checks collisions with the same hash at compile time; the runtime must agree.
        Assert.That(MessageType<Add>.Id, Is.EqualTo(WireIds.Fnv1a("DaServer.Tests.Add")));
        Assert.That(MessageType<SessionClosed>.Id, Is.EqualTo(WireIds.Fnv1a("DaServer.Rpc.SessionClosed")));
    }
}
