using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using DaServer.Buffers;
using DaServer.Concurrency;
using DaServer.Rpc;
using Microsoft.Extensions.ObjectPool;

namespace DaServer.Benchmark;

/// <summary>
/// The shared pools under concurrent callers (1, 4 and 16 threads at once, as shards and IO threads do): each thread
/// rents 16 frames (frames in flight) and disposes them, or registers 16 calls in the call table and takes them back.
/// The mean is per frame or per call; contention shows as the mean growing with the threads. The <c>Bcl</c> methods run
/// the same pattern on the official types the framework's own replace (why not the BCL, in numbers): a pool bounded as
/// a node's (65,536 objects in flight), the shared array pool (not pinned), an unbounded and a bounded queue.
/// </summary>
[MemoryDiagnoser]
public class PoolBenchmark
{
    private const int Rounds = 2_000, Batch = 16;

    private readonly PendingCallTable _table = new(16, stripes: 16); // as a node has it
    private readonly DaServer.Concurrency.ObjectPool<Item> _objects = new(static () => new Item(), 1 << 16);
    private readonly DefaultObjectPool<Item> _objectsBcl = new(new DefaultPooledObjectPolicy<Item>(), 1 << 16);
    private readonly MpmcQueue<long> _queue = new(1 << 16);
    private readonly ConcurrentQueue<long> _queueBcl = new();
    private readonly Channel<long> _channelBcl = Channel.CreateBounded<long>(1 << 16);
    private readonly IPendingCall _call = PendingCall<int>.Rent(null);
    private ParallelOptions _options = null!;

    [Params(1, 4, 16)] public int Threads;

    [GlobalSetup]
    public void Setup() => _options = new ParallelOptions { MaxDegreeOfParallelism = Threads };

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void Frames() => Parallel.For(0, 16, _options, static _ =>
    {
        Span<FrameWriter?> held = new FrameWriter?[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) held[i] = FrameWriter.Rent();
            for (int i = 0; i < Batch; i++) held[i]!.Dispose();
        }
    });

    /// <summary>Send batches as sockets use them: 4 KB buffers rented and returned by many threads at once.</summary>
    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void Buffers() => Parallel.For(0, 16, _options, static _ =>
    {
        Span<byte[]?> held = new byte[]?[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) held[i] = BufferPool.Rent(4096);
            for (int i = 0; i < Batch; i++) BufferPool.Return(held[i]);
        }
    });

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void BuffersBcl() => Parallel.For(0, 16, _options, static _ =>
    {
        Span<byte[]?> held = new byte[]?[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) held[i] = ArrayPool<byte>.Shared.Rent(4096);
            for (int i = 0; i < Batch; i++) ArrayPool<byte>.Shared.Return(held[i]!);
        }
    });

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void Objects() => Parallel.For(0, 16, _options, _ =>
    {
        Span<Item?> held = new Item?[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) held[i] = _objects.Rent();
            for (int i = 0; i < Batch; i++) _objects.Return(held[i]!);
        }
    });

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void ObjectsBcl() => Parallel.For(0, 16, _options, _ =>
    {
        Span<Item?> held = new Item?[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) held[i] = _objectsBcl.Get();
            for (int i = 0; i < Batch; i++) _objectsBcl.Return(held[i]!);
        }
    });

    /// <summary>Many threads post work items and take them back, as shards' dispatch queues are used.</summary>
    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void Queue() => Parallel.For(0, 16, _options, _ =>
    {
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) _queue.TryEnqueue(i);
            for (int i = 0; i < Batch; i++) _queue.TryDequeue(out _);
        }
    });

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void QueueBcl() => Parallel.For(0, 16, _options, _ =>
    {
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) _queueBcl.Enqueue(i);
            for (int i = 0; i < Batch; i++) _queueBcl.TryDequeue(out _);
        }
    });

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void ChannelBcl() => Parallel.For(0, 16, _options, _ =>
    {
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) _channelBcl.Writer.TryWrite(i);
            for (int i = 0; i < Batch; i++) _channelBcl.Reader.TryRead(out _);
        }
    });

    [Benchmark(OperationsPerInvoke = Rounds * Batch * 16)]
    public void Calls() => Parallel.For(0, 16, _options, _ =>
    {
        Span<uint> ids = stackalloc uint[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) _table.TryRegister(_call, 0, out ids[i]);
            for (int i = 0; i < Batch; i++) _table.Take(ids[i]);
        }
    });

    public sealed class Item
    {
        public long Value;
    }
}
