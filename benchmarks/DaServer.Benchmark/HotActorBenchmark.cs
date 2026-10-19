using System.Runtime.CompilerServices;
namespace DaServer.Benchmark;

/// <summary>
/// One hot actor (a map thousands enter, a sale's stock): 1,024 concurrent callers, each entering a gate that must
/// wait for another node's acknowledgement (a ledger on another node: the round trip a BindSession costs). The gate
/// never waits (ADR-080: no await in handlers), so both modes take the next caller at once and differ in how the reply
/// is given: Answered, the gate passes the caller's reply token with its ask and replies from its
/// <c>Answered&lt;Add, int&gt;</c> handler (one more message through the gate per call); ReplyAfter, the runtime
/// replies when the ask completes. One operation = 1,024 calls issued and all completed.
/// (Before ADR-080 the modes were Awaited, the gate keeping its turn for every round trip, and Deferred, ReplyWhen:
/// ReplyAfter is Deferred's successor and compares with it; Answered replaces Awaited, whose turn-holding wait no
/// longer exists.)
/// </summary>
[MemoryDiagnoser]
public class HotActorBenchmark
{
    private const int Callers = 1024;

    public enum ReplyMode
    {
        Answered,
        ReplyAfter,
    }

    private Node[] _nodes = null!;
    private GateId _gate;
    private CounterId[] _ledgers = null!;
    private readonly Reply<bool>[] _calls = new Reply<bool>[Callers];

    [Params(ReplyMode.Answered, ReplyMode.ReplyAfter)] public ReplyMode Mode;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(2, shards: 4).GetAwaiter().GetResult();
        _gate = Clusters.IdOwnedBy<GateId>(_nodes[0], _nodes[0]);
        _ledgers = new CounterId[64]; // the acknowledgements come from many actors: the gate is the only hot spot
        long key = 0;
        for (int i = 0; i < _ledgers.Length; i++)
        {
            _ledgers[i] = Clusters.IdOwnedBy<CounterId>(_nodes[0], _nodes[1], key + 1);
            key = _ledgers[i].Value;
            _nodes[0].Ask(_ledgers[i], new Add(0)).AsTask().GetAwaiter().GetResult();
        }

        _nodes[0].Ask(_gate, new EnterReplyAfter(_ledgers[0])).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes);

    [Benchmark, AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask Enter()
    {
        var caller = _nodes[0];
        for (int i = 0; i < Callers; i++)
        {
            var ledger = _ledgers[i % _ledgers.Length];
            _calls[i] = Mode == ReplyMode.Answered ? caller.Ask(_gate, new EnterAnswered(ledger)) : caller.Ask(_gate, new EnterReplyAfter(ledger));
        }

        for (int i = 0; i < Callers; i++) await _calls[i];
    }
}
