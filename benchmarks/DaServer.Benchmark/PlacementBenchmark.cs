using DaServer.Cluster;

namespace DaServer.Benchmark;

/// <summary>
/// Finding an actor's node in a view of <see cref="Nodes"/> members hosting its type: what every send to an actor and
/// every message a node receives for one does. One operation = one lookup (keys vary, as real traffic does).
/// </summary>
[MemoryDiagnoser]
public class PlacementBenchmark
{
    private const int Keys = 1024;
    private const uint HandlerType = 0x5EED;

    private ClusterView _view = null!;

    [Params(3, 30, 300)] public int Nodes;

    [GlobalSetup]
    public void Setup()
    {
        var members = new ClusterMember[Nodes];
        for (int i = 0; i < Nodes; i++)
            members[i] = new ClusterMember
            {
                NodeId = (ulong)(i + 1) * 0x9E3779B97F4A7C15UL,
                Status = MemberStatus.Up,
                Handlers = [new HostedMessage { MessageId = 1, HandlerTypeId = HandlerType, Actor = true }],
            };
        _view = new ClusterView(members);
    }

    [Benchmark(OperationsPerInvoke = Keys)]
    public ulong Owner()
    {
        ulong sum = 0;
        for (long key = 0; key < Keys; key++) sum += _view.HandlerTypeOwner(HandlerType, key * 7919)!.NodeId;
        return sum;
    }
}
