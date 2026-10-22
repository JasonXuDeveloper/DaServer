namespace DaServer.Tests;

/// <summary>Unit tests of the runtime building blocks: queues, buffers and frames here, the rest by component.</summary>
public partial class InfrastructureTests
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

    /// <summary>
    /// More threads than cores, each holding at most one item in a queue with room for all: never full, and never
    /// empty while the thread's own item is in it. A thread preempted between taking a position and handing its slot
    /// over must not make the others see the queue full or empty.
    /// </summary>
    [Test]
    public void MpmcQueueNeverFalselyFullOrEmpty()
    {
        int threads = Environment.ProcessorCount * 4;
        var q = new MpmcQueue<long>(threads * 2);
        const int perThread = 200_000;
        long falseFull = 0, falseEmpty = 0;
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (long i = 0; i < perThread; i++)
                {
                    if (!q.TryEnqueue(i))
                    {
                        Interlocked.Increment(ref falseFull);
                        continue;
                    }

                    if (!q.TryDequeue(out _)) Interlocked.Increment(ref falseEmpty);
                }
            });
            workers[t].Start();
        }

        foreach (var w in workers) w.Join();
        Assert.That((falseFull, falseEmpty), Is.EqualTo((0L, 0L)));
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
        w.Write(new string('x', 5000));
        w.SetHeader(FrameKind.Message, 0, 7, 9);
        var header = FrameHeader.Read(w.Frame);
        Assert.That(header.Length, Is.EqualTo(w.Length));
        var reader = new Nino.Core.Reader(w.Payload);
        string text = Nino.Core.NinoDeserializer.Deserialize<string>(ref reader);
        Assert.That(text, Has.Length.EqualTo(5000));
    }

    [Test]
    public void ALargeFrameGivesItsArrayBackAndStillAllocatesNothing()
    {
        // A pooled writer keeps at most 4 KB: a 32 KB frame's array goes back to ArrayPool on dispose (the pool's memory
        // stays bounded whatever the bursts were) and the next large frame rents one from there, without garbage.
        byte[] large = new byte[32 * 1024];
        void Frame()
        {
            using var w = FrameWriter.Rent();
            w.Write(large);
            w.SetHeader(FrameKind.Message, 0, 7, 9);
        }

        for (int i = 0; i < 100; i++) Frame(); // the pool's buckets fill
        // The pool is the process's: a buffer another thread holds meanwhile makes this one grow it (kept for reuse, not
        // garbage), so its growth is told apart.
        long before = GC.GetAllocatedBytesForCurrentThread(), grown = BufferPool.AllocatedBytes;
        for (int i = 0; i < 1000; i++) Frame();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before - (BufferPool.AllocatedBytes - grown);
        Assert.That(allocated / 1000.0, Is.LessThan(1), $"{allocated} B for 1,000 frames of 32 KB");
    }

    [Test]
    public void APeaksBuffersGoBackHalfPerTrimWhileUsedOnesStay()
    {
        // 2 MB buffers: a bucket nothing else in the process uses. A peak of 30 (the bucket holds 32), then 10 in use every period.
        const int size = 2 << 20;
        var peak = new byte[30][];
        for (int i = 0; i < peak.Length; i++) peak[i] = BufferPool.Rent(size);
        foreach (var buffer in peak) BufferPool.Return(buffer);
        int held = BufferPool.Retained(size);
        Assert.That(held, Is.GreaterThanOrEqualTo(30));
        BufferPool.Trim(); // the peak emptied the bucket: everything was used, nothing goes

        Assert.That(BufferPool.Retained(size), Is.EqualTo(held), "a period whose low-water mark is zero releases nothing");
        void Period()
        {
            var used = new byte[10][];
            for (int i = 0; i < used.Length; i++) used[i] = BufferPool.Rent(size);
            foreach (var buffer in used) BufferPool.Return(buffer);
            BufferPool.Trim();
        }

        Period(); // 10 of `held` used: half of the other held - 10 go
        int after = BufferPool.Retained(size);
        Assert.That(after, Is.EqualTo(held - (held - 10) / 2));
        for (int i = 0; i < 10; i++) Period();
        Assert.That(BufferPool.Retained(size), Is.InRange(10, 11), "down to what each period uses, never below it");
        Period();
        Assert.That(BufferPool.AllocatedBytes, Is.Not.Zero);
    }

    [Test]
    public void TrailersCarryTheOriginAndAPerHopCallId()
    {
        using var w = FrameWriter.Rent();
        w.Write(new Add(5));
        int payload = w.Payload.Length;
        w.SetCallId(41);
        w.SetOrigin(new Origin(7, 8)); // after the call id: the call id moves behind it
        w.SetHeader(FrameKind.Message, 3, TypeId<Add>.Value, 9);
        var header = FrameHeader.Read(w.Frame);
        Assert.That(header.IsRequest, Is.True);
        Assert.That(header.Flags, Is.EqualTo(FrameFlags.HasCall | FrameFlags.HasOrigin));
        Assert.That(FrameHeader.CallIdOf(in header, w.Frame), Is.EqualTo(41));
        Assert.That(FrameHeader.OriginOf(in header, w.Frame), Is.EqualTo(new Origin(7, 8)));
        Assert.That(FrameHeader.PayloadOf(in header, w.Frame).Length, Is.EqualTo(payload), "the trailer is not payload");

        // A relaying hop copies the frame and rewrites the call id in place: the frame does not grow.
        using var relayed = FrameWriter.CopyOf(w.Frame);
        relayed.SetCallId(77);
        relayed.SetHeader(FrameKind.Message, 3, TypeId<Add>.Value, 9, hops: 1);
        Assert.That(relayed.Length, Is.EqualTo(w.Length));
        Assert.That(relayed.CallId, Is.EqualTo(77));
        Assert.That(relayed.Origin, Is.EqualTo(new Origin(7, 8)), "the origin travels on");
        Assert.That(relayed.Payload.SequenceEqual(w.Payload), Is.True);
        var reader = new Nino.Core.Reader(relayed.Payload);
        Add add = Nino.Core.NinoDeserializer.Deserialize<Add>(ref reader);
        Assert.That(add.Delta, Is.EqualTo(5));
    }
}
