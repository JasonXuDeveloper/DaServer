using DaServer.Buffers;
using DaServer.Rpc;

namespace DaServer.Benchmark;

/// <summary>
/// The shared pools under concurrent callers (1, 4 and 16 threads at once, as shards and IO threads do): each thread
/// rents 16 frames (frames in flight) and disposes them, or registers 16 calls in the call table and takes them back.
/// The mean is per frame or per call; contention shows as the mean growing with the threads.
/// </summary>
[MemoryDiagnoser]
public class PoolBenchmark
{
    private const int Rounds = 2_000, Batch = 16;

    private readonly PendingCallTable _table = new();
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
    public void Calls() => Parallel.For(0, 16, _options, _ =>
    {
        Span<uint> ids = stackalloc uint[Batch];
        for (int r = 0; r < Rounds; r++)
        {
            for (int i = 0; i < Batch; i++) _table.TryRegister(_call, 0, out ids[i]);
            for (int i = 0; i < Batch; i++) _table.Take(ids[i]);
        }
    });
}
