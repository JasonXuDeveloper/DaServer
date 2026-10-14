using System.Net;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DaServer.Client;
using DaServer.Runtime;

namespace DaServer.Benchmark;

/// <summary>
/// Gateway fan-out: a game node broadcasts to a group of <see cref="Clients"/> real client sockets connected to one
/// gateway. One operation = one broadcast fully delivered to every client (serialized once, one frame to the gateway,
/// one copy per session).
/// </summary>
[MemoryDiagnoser]
public class BroadcastBenchmark
{
    private const long Room = 1;

    private sealed class Receiver(Countdown countdown) : IRoomClient
    {
        public void OnTick(int sequence) => countdown.Signal();
    }

    private readonly Countdown _countdown = new();
    private Node[] _nodes = null!;
    private GameClient[] _clients = null!;
    private RoomProxy _room;
    private int _sequence;

    [Params(1_000, 5_000)] public int Clients;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _nodes = await Clusters.Start(2, shards: 4, gateway: true);
        var gateway = new IPEndPoint(IPAddress.Loopback, _nodes[0].ClientPort);
        var receiver = new Receiver(_countdown);

        _clients = new GameClient[Clients];
        for (int i = 0; i < Clients; i++)
        {
            var client = _clients[i] = new GameClient();
            await client.ConnectAsync(gateway);
            client.Register<IRoomClient>(receiver);
            await client.Room().Join(Room);
        }

        _room = _nodes[1].Room(); // broadcast from the non-gateway node: crosses the node link
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
        var delivered = _countdown.Reset(Clients);
        await _room.Broadcast(Room, ++_sequence);
        await delivered;
    }
}
