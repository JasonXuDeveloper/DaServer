namespace DaServer.Benchmark;

/// <summary>
/// Keyed cluster events: a burst of <see cref="Burst"/> events published to 64 actors owned by the publishing node
/// (shard mailboxes) or by another node (TCP link), fully handled. One operation = one event (published, routed by
/// key, delivered to the activation's mailbox, handled).
/// </summary>
[MemoryDiagnoser]
public class EventBenchmark
{
    private const int Actors = 64;
    private const int Burst = 1024;

    private Node[] _nodes = null!;
    private long[] _keys = null!;
    private ValueTask<int>[] _flush = null!;

    [Params(RpcBenchmark.Target.Local, RpcBenchmark.Target.Remote)] public RpcBenchmark.Target Placement;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(2, shards: 4).GetAwaiter().GetResult();
        var publisher = _nodes[0];
        var owner = Placement == RpcBenchmark.Target.Local ? _nodes[0] : _nodes[1];
        _keys = new long[Actors];
        _flush = new ValueTask<int>[Actors];
        long key = 0;
        for (int i = 0; i < Actors; i++)
        {
            _keys[i] = key = Clusters.KeyOwnedBy<Counter>(publisher, owner, key + 1);
            publisher.Ask(key, new Add(0)).AsTask().GetAwaiter().GetResult(); // activate
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes).GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task Events()
    {
        var cluster = _nodes[0];
        for (int i = 0; i < Burst; i++) cluster.Publish(_keys[i % Actors], new Bumped(1));

        // Messages of one sender reach an actor in order: when the requests return, every event was handled.
        for (int i = 0; i < Actors; i++) _flush[i] = cluster.Ask(_keys[i], new Add(0));
        for (int i = 0; i < Actors; i++) await _flush[i];
    }
}
