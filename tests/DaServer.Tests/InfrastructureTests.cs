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

    [Test]
    public void PendingCallTableCompletesTimesOutAndRejectsStaleIds()
    {
        var table = new PendingCallTable(4);
        var call = PendingCall<int>.Rent(null);
        var task = call.Task;
        Assert.That(table.TryRegister(call, deadlineMs: 0, out uint id), Is.True);

        var w = FrameWriter.Rent();
        w.Write(41);
        Assert.That(table.TryComplete(id, RpcStatus.Ok, w.Payload), Is.True);
        Assert.That(table.TryComplete(id, RpcStatus.Ok, w.Payload), Is.False, "completed once only");
        w.Dispose();
        Assert.That(task.AsTask().Result, Is.EqualTo(41));

        // Slot reuse: a late response for the old id must not complete the new call.
        var second = PendingCall<int>.Rent(null);
        Assert.That(table.TryRegister(second, deadlineMs: Environment.TickCount64 - 1, out uint id2), Is.True);
        Assert.That(id2, Is.Not.EqualTo(id));
        Assert.That(table.Take(id), Is.Null);
        Assert.That(table.SweepExpired(), Is.EqualTo(1));
        var timedOut = Assert.ThrowsAsync<RpcException>(async () => await second.Task);
        Assert.That(timedOut!.Status, Is.EqualTo(RpcStatus.Timeout));
        Assert.That(timedOut.Detail, Is.Null, "runtime failures are status-only");

        // A full table refuses instead of throwing (the caller fails the call as Overloaded).
        var held = PendingCall<int>.Rent(null);
        for (int i = 0; i < 16; i++) Assert.That(table.TryRegister(held, deadlineMs: 0, out _), Is.True);
        Assert.That(table.TryRegister(held, deadlineMs: 0, out _), Is.False);
    }

    [Test]
    public void PendingCallTableMemoryFollowsCallsInFlight()
    {
        var table = new PendingCallTable(16);
        Assert.That(table.Capacity, Is.Zero, "nothing allocated before the first call");
        var held = PendingCall<int>.Rent(null);
        var ids = new List<uint>();
        for (int i = 0; i < 3; i++)
        {
            Assert.That(table.TryRegister(held, deadlineMs: 0, out uint id), Is.True);
            ids.Add(id);
        }

        Assert.That(table.Capacity, Is.EqualTo(PendingCallTable.FirstChunk));
        foreach (var id in ids) table.Take(id);
        for (int round = 0; round < 1000; round++) // reuse: a steady call rate stays in the first chunk
        {
            Assert.That(table.TryRegister(held, deadlineMs: 0, out uint id), Is.True);
            table.Take(id);
        }

        Assert.That(table.Capacity, Is.EqualTo(PendingCallTable.FirstChunk));

        for (int i = 0; i < 100; i++) Assert.That(table.TryRegister(held, deadlineMs: 0, out _), Is.True);
        Assert.That(table.Capacity, Is.EqualTo(120), "chunks of 8, 16, 32, 64 slots");

        var full = new PendingCallTable(4); // 16 slots: chunks of 8 and 8, not 8 and 16
        for (int i = 0; i < 16; i++) Assert.That(full.TryRegister(held, deadlineMs: 0, out _), Is.True);
        Assert.That(full.Capacity, Is.EqualTo(16), "the last chunk ends at the table's size");
        Assert.That(full.TryRegister(held, deadlineMs: 0, out _), Is.False);
    }

    [TestCase(1)]
    [TestCase(8)]
    public void PendingCallTableHandsEachFreeSlotToOneCallerUnderContention(int stripes)
    {
        const int threads = 8, rounds = 50_000, inFlight = 20;
        var table = new PendingCallTable(10, stripes);
        var held = PendingCall<int>.Rent(null);
        int duplicates = 0, failures = 0;
        Parallel.For(0, threads, _ =>
        {
            var mine = new uint[inFlight];
            for (int round = 0; round < rounds; round++)
            {
                for (int i = 0; i < inFlight; i++)
                    if (!table.TryRegister(held, deadlineMs: 0, out mine[i])) Interlocked.Increment(ref failures);
                for (int i = 0; i < inFlight; i++)
                    if (table.Take(mine[i]) == null) Interlocked.Increment(ref duplicates); // someone else held the slot
            }
        });

        Assert.That(failures, Is.Zero, "8 x 20 calls fit in 1024 slots");
        Assert.That(duplicates, Is.Zero, "a slot was handed to two calls");
        Assert.That(table.Capacity, Is.LessThanOrEqualTo(2 * threads * inFlight + PendingCallTable.FirstChunk));
    }

    [Test]
    public void SlotsFreedOnOtherThreadsAreReusedNotGrown()
    {
        // Callers register on their threads, replies free the slots on others (IO threads): each stripe fills on the
        // freeing threads' side and the callers take from there, so the table does not grow past its calls in flight.
        const int callers = 4, rounds = 2_000, inFlight = 16;
        var table = new PendingCallTable(12, stripes: 8);
        var held = PendingCall<int>.Rent(null);
        var replies = new System.Collections.Concurrent.BlockingCollection<uint>(callers * inFlight);
        var replier = Task.Run(() =>
        {
            foreach (uint id in replies.GetConsumingEnumerable()) table.Take(id);
        });
        Parallel.For(0, callers, _ =>
        {
            for (int round = 0; round < rounds; round++)
                for (int i = 0; i < inFlight; i++)
                {
                    uint id;
                    while (!table.TryRegister(held, deadlineMs: 0, out id)) Thread.Yield();
                    replies.Add(id);
                }
        });
        replies.CompleteAdding();
        replier.Wait();
        Assert.That(table.Capacity, Is.LessThanOrEqualTo(2 * (callers * inFlight + callers) + PendingCallTable.FirstChunk),
            "slots freed on the replier's stripe were taken again");
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
        int rents = window.SharedRents;
        long garbage = GC.GetAllocatedBytesForCurrentThread();
        Stream();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - garbage;
        Assert.That(window.SharedRents, Is.EqualTo(rents), "no chunk rented from the shared pool");
        Assert.That(allocated, Is.Zero, "nothing allocated");

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
    public void AHandleCancelsOnlyOnTheWheelThatMadeIt()
    {
        // An actor that moved may keep a handle from its previous node: on its new node's wheel it must not cancel the
        // timer of the same entry there.
        var first = new TimerWheel<int>(TimeSpan.FromMilliseconds(5));
        var second = new TimerWheel<int>(TimeSpan.FromMilliseconds(5));
        long stale = first.Schedule(TimeSpan.FromSeconds(1), 1);
        long own = second.Schedule(TimeSpan.FromSeconds(1), 2);
        Assert.That(second.Cancel(stale), Is.False, "another wheel's handle");
        Assert.That(second.Cancel(own), Is.True);
    }

    [Test]
    public void TimerWheelsFireDueTimersOnceAndCancelOthers()
    {
        var wheel = new TimerWheel<int>(TimeSpan.FromMilliseconds(5), slots: 8); // 40 ms a round: 100 ms is a later round
        var fired = new Fired();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        wheel.Schedule(TimeSpan.FromMilliseconds(30), 3);
        wheel.Schedule(TimeSpan.FromMilliseconds(10), 1);
        long cancelled = wheel.Schedule(TimeSpan.FromMilliseconds(20), 2);
        wheel.Schedule(TimeSpan.FromMilliseconds(100), 4);
        Assert.That(wheel.Cancel(cancelled), Is.True);
        Assert.That(wheel.Cancel(cancelled), Is.False, "once");

        while (wheel.Count > 0 && watch.ElapsedMilliseconds < 2000) wheel.Advance(ref fired);
        Assert.That(fired.Items, Is.EqualTo(new[] { 1, 3, 4 }));
        Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(100), "nothing fires early");

        // Steady state: schedule and fire without allocating once the entry table reached its peak (the first round).
        var counting = new Counting();
        long before = 0;
        for (int round = 0; round < 2; round++)
        {
            if (round == 1) before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) wheel.Schedule(TimeSpan.Zero, i);
            while (wheel.Count > 0) wheel.Advance(ref counting);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(counting.Count, Is.EqualTo(2000));
    }

    private struct Fired() : ITimerHandler<int>
    {
        public readonly List<int> Items = [];

        public void OnTimer(in int item) => Items.Add(item);
    }

    private struct Counting : ITimerHandler<int>
    {
        public int Count;

        public void OnTimer(in int item) => Count++;
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
        var pulse = new MemberPulse(0xDEADBEEF12345678, 7, 1L << 40, MemberStatus.Suspect, 123, 4567, -1, int.MaxValue);
        Span<byte> wire = stackalloc byte[MemberPulse.Size];
        pulse.Write(wire);
        Assert.That(MemberPulse.Read(wire), Is.EqualTo(pulse));
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
    public void OptionsANodeCannotRunWithAreRefusedAtOnce()
    {
        var error = Assert.Throws<ArgumentException>(() => new Node(new NodeOptions
            { TickRate = 0, SuspectAfter = TimeSpan.FromSeconds(5), DeadAfter = TimeSpan.FromSeconds(2) }));
        Assert.That(error!.Message, Does.Contain("TickRate 0").And.Contain("below DeadAfter"), "every problem in one message");
    }

    [TestCase("game-0:7000", "game-0", 7000)]
    [TestCase("10.0.0.5:7000", "10.0.0.5", 7000)]
    [TestCase("[fd00::5]:7000", "fd00::5", 7000)]
    public void HostPortParses(string text, string host, int port)
    {
        Assert.That(DaServer.Net.HostPort.TryParse(text, out var h, out int p), Is.True);
        Assert.That((h, p), Is.EqualTo((host, port)));
    }

    [TestCase("game-0")]
    [TestCase("game-0:")]
    [TestCase(":7000")]
    [TestCase("game-0:70000")]
    [TestCase("fe80::1")]
    [TestCase("fd00::5:7000")]
    public void HostPortRefuses(string text) => Assert.That(DaServer.Net.HostPort.TryParse(text, out _, out _), Is.False);

    [Test]
    public void TypeIdsAreNinosTypeIds()
    {
        // The analyzer checks collisions with the same hash at compile time; the runtime and Nino must agree.
        Assert.That(TypeId<Add>.Value, Is.EqualTo((uint)Nino.Core.NinoTypeId.Compute("DaServer.Tests.Add")));
        Assert.That(TypeId<SessionClosed>.Value, Is.EqualTo((uint)Nino.Core.NinoTypeId.Of(typeof(SessionClosed))));
        // A renamed type keeps its identity (and its stored records) with [NinoFormerName].
        Assert.That(TypeId<Renamed>.Value, Is.EqualTo((uint)Nino.Core.NinoTypeId.Compute("Game.Before.Rename")));
    }

    [Nino.Core.NinoFormerName("Game.Before.Rename")]
    private struct Renamed;
}
