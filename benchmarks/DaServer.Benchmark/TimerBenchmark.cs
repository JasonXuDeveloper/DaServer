namespace DaServer.Benchmark;

/// <summary>
/// Delayed sends (timers as data): an actor sends 2,000 one-way messages to another actor of its node, now or through
/// its shard's timer wheel with no delay, until all arrived. Delayed sends wait for the wheel's next slot (5 ms), so the
/// mean per message is mostly that wait shared by 2,000 sends; both allocate nothing.
/// </summary>
[MemoryDiagnoser]
public class TimerBenchmark
{
    private const int Count = 2_000; // below the target's mailbox bound

    private Node[] _nodes = null!;
    private SchedulerId _scheduler;
    private CounterId _target;
    private int _expected;

    [Params(false, true)] public bool Delayed;

    [GlobalSetup]
    public void Setup()
    {
        _nodes = Clusters.Start(1, shards: 4).GetAwaiter().GetResult();
        _scheduler = new SchedulerId(1);
        _target = new CounterId(2);
        _expected = _nodes[0].Ask(_target, new Add(0)).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes).GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = Count)]
    public async Task Sends()
    {
        var node = _nodes[0];
        await node.Ask(_scheduler, new Bumps(_target, Count, Delayed));
        _expected += Count;
        while (await node.Ask(_target, new Add(0)) < _expected) { }
    }
}
