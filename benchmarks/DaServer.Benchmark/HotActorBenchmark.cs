namespace DaServer.Benchmark;

/// <summary>
/// One hot actor (a map thousands enter, a sale's stock): 1,024 concurrent callers, each entering a gate that must
/// wait for another node's acknowledgement (a ledger on another node: the round trip a BindSession costs). Awaited:
/// the gate keeps its turn for every round trip, so callers queue behind it one round trip each. Deferred: the gate
/// replies with ReplyWhen and takes its next caller at once. One operation = 1,024 calls issued and all completed.
/// </summary>
[MemoryDiagnoser]
public class HotActorBenchmark
{
    private const int Callers = 1024;

    public enum ReplyMode
    {
        Awaited,
        Deferred,
    }

    private Node[] _nodes = null!;
    private GateId _gate;
    private CounterId[] _ledgers = null!;
    private readonly Reply<bool>[] _calls = new Reply<bool>[Callers];

    [Params(ReplyMode.Awaited, ReplyMode.Deferred)] public ReplyMode Mode;

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

        _nodes[0].Ask(_gate, new EnterDeferred(_ledgers[0])).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes);

    [Benchmark]
    public async Task Enter()
    {
        var caller = _nodes[0];
        for (int i = 0; i < Callers; i++)
        {
            var ledger = _ledgers[i % _ledgers.Length];
            _calls[i] = Mode == ReplyMode.Awaited ? caller.Ask(_gate, new EnterAwaited(ledger)) : caller.Ask(_gate, new EnterDeferred(ledger));
        }

        for (int i = 0; i < Callers; i++) await _calls[i];
    }
}
