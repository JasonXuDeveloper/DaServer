using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DaServer.Runtime;

namespace DaServer.Benchmark;

/// <summary>
/// Request/response throughput and latency: <see cref="Concurrency"/> outstanding calls to actors on the calling node
/// (shard mailboxes, no socket) or on another node (batched TCP link, both directions). One operation =
/// <see cref="Concurrency"/> calls issued together and all completed.
/// </summary>
[MemoryDiagnoser]
public class RpcBenchmark
{
    public enum Target
    {
        Local,
        Remote,
    }

    private Node[] _nodes = null!;
    private CounterProxy[] _proxies = null!;
    private ValueTask<int>[] _calls = null!;

    [Params(1, 64, 1024)] public int Concurrency;

    [Params(Target.Local, Target.Remote)] public Target Placement;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(2, shards: 4).GetAwaiter().GetResult();
        var caller = _nodes[0];
        var owner = Placement == Target.Local ? _nodes[0] : _nodes[1];

        // One actor per outstanding call, all owned by the target node, spread over its shards.
        _proxies = new CounterProxy[Concurrency];
        long key = 0;
        for (int i = 0; i < Concurrency; i++)
        {
            do key++; while (caller.View.ActorOwner(CounterRpc.ContractId, key)!.NodeId != owner.Id);
            _proxies[i] = caller.Counter(key);
            _proxies[i].Add(0).AsTask().GetAwaiter().GetResult(); // activate
        }

        _calls = new ValueTask<int>[Concurrency];
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes).GetAwaiter().GetResult();

    [Benchmark]
    public async Task Calls()
    {
        for (int i = 0; i < _calls.Length; i++) _calls[i] = _proxies[i].Add(1);
        for (int i = 0; i < _calls.Length; i++) await _calls[i];
    }
}
