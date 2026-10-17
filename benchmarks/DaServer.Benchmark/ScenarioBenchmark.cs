using DaServer.Client;

namespace DaServer.Benchmark;

/// <summary>
/// The realm broadcast of the 10k-player scenario (samples: every 5 ticks to every session): a game node and three
/// gateways, <see cref="Clients"/> real client sockets spread over the gateways. One operation = one broadcast to
/// <see cref="ClientGroup.All"/> from the game node, received by every client (serialized once, one frame per gateway,
/// one copy per session). Allocations include the clients' receive path. The full scenario with 10k bots in separate
/// processes is Sample.Bot; 9k is the most one process can hold here (both socket ends, 20k descriptors).
/// </summary>
[MemoryDiagnoser, ThreadingDiagnoser]
public class ScenarioBenchmark
{
    private readonly Countdown _countdown = new();
    private Node[] _nodes = null!;
    private ClusterClient[] _clients = null!;
    private int _sequence;

    [Params(3_000, 9_000)] public int Clients;

    /// <summary>Client writes on a beat of this many ms (ClientConnectionOptions.FlushInterval; 0: at once).</summary>
    [Params(0, 2)] public int FlushMs;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _nodes = await Clusters.Start(4, shards: 4, gateways: 3, clientFlush: TimeSpan.FromMilliseconds(FlushMs));
        _clients = await Clusters.Connect(Clients, _nodes[..3], new Receiver(_countdown));
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes, _clients);

    [Benchmark]
    public ValueTask BroadcastToEveryone()
    {
        var delivered = _countdown.Reset(Clients);
        _nodes[3].Send(ClientGroup.All, new Tick(++_sequence)); // from the game node, like the realm system
        return delivered;
    }
}
