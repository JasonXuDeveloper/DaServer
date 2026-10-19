namespace DaServer.Tests;

/// <summary>The pending call table and the resend windows.</summary>
public partial class InfrastructureTests
{
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
    public void AFailedCallsSlotCoolsSoItsLateReplyFindsNoOtherCall()
    {
        // 4 index bits leave 28 bits of generation, but the guarantee must not rest on them: a timed-out call's slot is
        // not reused while its reply may still come, so its id stays unanswerable however many calls go by.
        var table = new PendingCallTable(4);
        var held = PendingCall<int>.Rent(null);
        Assert.That(table.TryRegister(PendingCall<int>.Rent(null), deadlineMs: Environment.TickCount64 - 1, out uint late), Is.True);
        Assert.That(table.SweepExpired(), Is.EqualTo(1));
        for (int i = 0; i < 1000; i++)
        {
            Assert.That(table.TryRegister(held, deadlineMs: 0, out uint id), Is.True);
            Assert.That(id & 15, Is.Not.EqualTo(late & 15), "the cooling slot is not handed out");
            table.Take(id);
        }

        Assert.That(table.Take(late), Is.Null, "the late reply finds nothing");
        Assert.That(table.SweepExpired(), Is.Zero, "a cooling slot is not a call");
        for (int i = 0; i < 16; i++) Assert.That(table.TryRegister(held, deadlineMs: 0, out _), Is.True, "a full table takes it back early");
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
}
