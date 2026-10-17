using DaServer.Client;

namespace DaServer.Benchmark;

/// <summary>
/// Gateway fan-out: an actor on a non-gateway node broadcasts to a group of <see cref="Clients"/> real client sockets connected to one
/// gateway. One operation = one broadcast to each of <see cref="Groups"/> groups every client joined (as a player
/// hears the 3x3 cells around it), fully delivered (each serialized once, one frame to the gateway, one copy per session).
/// </summary>
[MemoryDiagnoser, ThreadingDiagnoser]
public class BroadcastBenchmark
{
    private readonly Countdown _countdown = new();
    private Node[] _nodes = null!;
    private ClusterClient[] _clients = null!;
    private RoomId _room;
    private int _sequence;

    [Params(1_000, 5_000)] public int Clients;

    /// <summary>Client writes on a beat of this many ms (ClientConnectionOptions.FlushInterval; 0: at once).</summary>
    [Params(0, 2)] public int FlushMs;

    [Params(1, 9)] public int Groups;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _nodes = await Clusters.Start(2, shards: 4, gateways: 1, clientFlush: TimeSpan.FromMilliseconds(FlushMs));

        // A room owned by the non-gateway node: the broadcast crosses the node link, then fans out at the gateway.
        var room = Clusters.IdOwnedBy<RoomId>(_nodes[0], _nodes[1]);
        _clients = await Clusters.Connect(Clients, _nodes[..1], new Receiver(_countdown));
        foreach (var client in _clients) await client.Ask(new Join(room, Groups));

        _room = room;
    }

    [GlobalCleanup]
    public void Cleanup() => Clusters.Stop(_nodes, _clients);

    [Benchmark]
    public async Task Broadcast()
    {
        var delivered = _countdown.Reset(Clients * Groups);
        await _nodes[1].Ask(_room, new Broadcast(Groups, ++_sequence));
        await delivered;
    }
}
