using DaServer.Net;

namespace DaServer.Tests;

/// <summary>The send side shared by TCP connections and WebSocket channels.</summary>
public class SendQueueTests
{
    [Test]
    public void APeerTooFarBehindOverflowsOnceThenIsRefused()
    {
        var queue = new SendQueue(maxPendingBytes: 64, bufferSize: 256, releaseIdle: true, keepSpare: false);
        var frame = new byte[40];
        Assert.That(queue.Append(frame), Is.EqualTo(SendQueue.Result.Wake), "the first bytes wake the writer");
        Assert.That(queue.Append(frame), Is.EqualTo(SendQueue.Result.Overflow), "past the bound: close the channel, once");
        Assert.That(queue.Append(frame), Is.EqualTo(SendQueue.Result.Closed));
        Assert.That(queue.TryTake(out var batch, out int length), Is.True);
        Assert.That(length, Is.EqualTo(40), "what was queued before the overflow is still written");
        queue.Release(batch);
    }

    [Test]
    public void ASealedQueueWritesWhatItHoldsAndRefusesTheRest()
    {
        // The fence of a move (a goodbye as the last frame): frames before it are written before it, none after it.
        var queue = new SendQueue(maxPendingBytes: 1024, bufferSize: 256, releaseIdle: true, keepSpare: false);
        Assert.That(queue.Append(new byte[16]), Is.EqualTo(SendQueue.Result.Wake));
        Assert.That(queue.Seal(new byte[8]), Is.EqualTo(SendQueue.Result.Queued), "the last frame queues behind the first");
        Assert.That(queue.Append(new byte[16]), Is.EqualTo(SendQueue.Result.Closed), "refused after the last frame");
        Assert.That(queue.Seal(new byte[8]), Is.EqualTo(SendQueue.Result.Closed), "one last frame");
        Assert.That(queue.TryTake(out var batch, out int length), Is.True);
        Assert.That(length, Is.EqualTo(24), "both frames are written, in order");
        queue.Release(batch);
    }

    [Test]
    public void ASendBatchWakesEachWriterOnceAtItsEnd()
    {
        var queue = new SendQueue(maxPendingBytes: 1024, bufferSize: 256, releaseIdle: true, keepSpare: false);
        var frame = new byte[16];
        ValueTask woken;
        using (SendQueue.BeginBatch())
        {
            Assert.That(queue.Append(frame), Is.EqualTo(SendQueue.Result.Wake));
            queue.Wake();
            Assert.That(queue.Append(frame), Is.EqualTo(SendQueue.Result.Queued));
            woken = queue.Signal.WaitAsync();
            Assert.That(woken.IsCompleted, Is.False, "not woken inside the batch");
        }

        Assert.That(woken.IsCompleted, Is.True, "woken when the batch ends");
        Assert.That(queue.TryTake(out var batch, out int length), Is.True);
        Assert.That(length, Is.EqualTo(32), "both frames leave in one write");
        queue.Release(batch);
        Assert.That(queue.TryTake(out _, out _), Is.False);
    }

    [Test]
    public void SteadySendsAllocateNothing()
    {
        var queue = new SendQueue(maxPendingBytes: 64 * 1024, bufferSize: 4096, releaseIdle: false, keepSpare: true);
        var frame = new byte[100];
        void Round()
        {
            for (int i = 0; i < 10; i++) queue.Append(frame);
            queue.TryTake(out var batch, out _);
            queue.Release(batch);
        }

        for (int i = 0; i < 3; i++) Round(); // the batch and the spare buffer, rented once
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Round();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }
}
