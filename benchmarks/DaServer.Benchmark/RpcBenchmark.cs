using System.Runtime.CompilerServices;
namespace DaServer.Benchmark;

/// <summary>
/// Request/response throughput and latency: <see cref="Concurrency"/> outstanding calls to actors on the calling node
/// (shard mailboxes, no socket) or on another node (batched TCP link, both directions). One operation =
/// <see cref="Concurrency"/> calls issued together and all completed.
/// </summary>
[MemoryDiagnoser, ThreadingDiagnoser]
public class RpcBenchmark
{
    public enum Target
    {
        Local,
        Remote,
    }

    private Node[] _nodes = null!;
    private CounterId[] _keys = null!;
    private Reply<int>[] _calls = null!;

    [Params(1, 64, 1024)] public int Concurrency;

    [Params(Target.Local, Target.Remote)] public Target Placement;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(2, shards: 4).GetAwaiter().GetResult();
        var caller = _nodes[0];
        var owner = Placement == Target.Local ? _nodes[0] : _nodes[1];

        // One actor per outstanding call, all owned by the target node, spread over its shards.
        _keys = new CounterId[Concurrency];
        long key = 0;
        for (int i = 0; i < Concurrency; i++)
        {
            _keys[i] = Clusters.IdOwnedBy<CounterId>(caller, owner, key + 1);
            key = _keys[i].Value;
            caller.Ask(_keys[i], new Add(0)).AsTask().GetAwaiter().GetResult(); // load the actor
        }

        _calls = new Reply<int>[Concurrency];
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes);

    [Benchmark, AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask Calls()
    {
        var caller = _nodes[0];
        for (int i = 0; i < _calls.Length; i++) _calls[i] = caller.Ask(_keys[i], new Add(1));
        for (int i = 0; i < _calls.Length; i++) await _calls[i];
    }
}
