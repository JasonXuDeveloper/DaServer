namespace DaServer.Benchmark;

/// <summary>
/// One-way messages: a burst of <see cref="Burst"/> messages sent to 64 actors owned by the sending node (shard
/// mailboxes) or by another node (TCP link), fully handled. One operation = one message (sent, routed by actor id,
/// delivered to the actor's mailbox, handled).
/// </summary>
[MemoryDiagnoser]
public class MessageBenchmark
{
    private const int Actors = 64;
    private const int Burst = 1024;

    private Node[] _nodes = null!;
    private CounterId[] _keys = null!;
    private Reply<int>[] _flush = null!;

    [Params(RpcBenchmark.Target.Local, RpcBenchmark.Target.Remote)] public RpcBenchmark.Target Placement;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(2, shards: 4).GetAwaiter().GetResult();
        var publisher = _nodes[0];
        var owner = Placement == RpcBenchmark.Target.Local ? _nodes[0] : _nodes[1];
        _keys = new CounterId[Actors];
        _flush = new Reply<int>[Actors];
        long key = 0;
        for (int i = 0; i < Actors; i++)
        {
            _keys[i] = Clusters.IdOwnedBy<CounterId>(publisher, owner, key + 1);
            key = _keys[i].Value;
            publisher.Ask(_keys[i], new Add(0)).AsTask().GetAwaiter().GetResult(); // load the actor
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes).GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task Messages()
    {
        var cluster = _nodes[0];
        for (int i = 0; i < Burst; i++) cluster.Send(_keys[i % Actors], new Bumped(1));

        // Messages of one sender reach an actor in order: when the requests return, every message was handled.
        for (int i = 0; i < Actors; i++) _flush[i] = cluster.Ask(_keys[i], new Add(0));
        for (int i = 0; i < Actors; i++) await _flush[i];
    }
}
