using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DaServer.Client;
using DaServer.Runtime;

namespace DaServer.Benchmark;

/// <summary>
/// Gateway fan-out: an actor on a non-gateway node broadcasts to a group of <see cref="Clients"/> real client sockets connected to one
/// gateway. One operation = one broadcast to each of <see cref="Groups"/> groups every client joined (as a player
/// hears the 3x3 cells around it), fully delivered (each serialized once, one frame to the gateway, one copy per session).
/// </summary>
[MemoryDiagnoser]
public class BroadcastBenchmark
{
    private sealed class Receiver(Countdown countdown) : IRoomClient
    {
        public void OnTick(int sequence) => countdown.Signal();
    }

    private readonly Countdown _countdown = new();
    private Node[] _nodes = null!;
    private ClusterClient[] _clients = null!;
    private RoomProxy _room;
    private int _sequence;

    [Params(1_000, 5_000)] public int Clients;

    [Params(1, 9)] public int Groups;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _nodes = await Clusters.Start(2, shards: 4, gateways: 1);

        // A room owned by the non-gateway node: the broadcast crosses the node link, then fans out at the gateway.
        long room = 1;
        while (_nodes[0].View.ActorOwner(RoomRpc.ContractId, room)!.NodeId != _nodes[1].Id) room++;
        var receiver = new Receiver(_countdown);

        _clients = new ClusterClient[Clients];
        for (int i = 0; i < Clients; i++)
        {
            var client = _clients[i] = new ClusterClient();
            await client.ConnectAsync("127.0.0.1", _nodes[0].ClientPort);
            client.Register<IRoomClient>(receiver);
            await client.Room(room).Join(Groups);
        }

        _room = _nodes[1].Room(room);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var client in _clients) client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Clusters.Stop(_nodes).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task Broadcast()
    {
        var delivered = _countdown.Reset(Clients * Groups);
        await _room.Broadcast(Groups, ++_sequence);
        await delivered;
    }
}
