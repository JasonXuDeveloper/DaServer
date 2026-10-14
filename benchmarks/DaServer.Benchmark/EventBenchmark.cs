using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DaServer.Runtime;

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
    private CounterProxy[] _proxies = null!;
    private ValueTask<int>[] _flush = null!;

    [Params(RpcBenchmark.Target.Local, RpcBenchmark.Target.Remote)] public RpcBenchmark.Target Placement;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(2, shards: 4).GetAwaiter().GetResult();
        var publisher = _nodes[0];
        var owner = Placement == RpcBenchmark.Target.Local ? _nodes[0] : _nodes[1];
        _keys = new long[Actors];
        _proxies = new CounterProxy[Actors];
        _flush = new ValueTask<int>[Actors];
        long key = 0;
        for (int i = 0; i < Actors; i++)
        {
            do key++; while (publisher.View.ActorOwner(CounterRpc.ContractId, key)!.NodeId != owner.Id);
            _keys[i] = key;
            _proxies[i] = publisher.Counter(key);
            _proxies[i].Add(0).AsTask().GetAwaiter().GetResult(); // activate
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes).GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task Events()
    {
        var cluster = _nodes[0];
        for (int i = 0; i < Burst; i++) cluster.Publish(new Bumped(_keys[i % Actors], 1));

        // Events and calls of one sender reach an actor in order: when the calls return, every event was handled.
        for (int i = 0; i < Actors; i++) _flush[i] = _proxies[i].Add(0);
        for (int i = 0; i < Actors; i++) await _flush[i];
    }
}
