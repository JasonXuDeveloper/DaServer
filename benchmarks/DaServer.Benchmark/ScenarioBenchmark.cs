using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using DaServer.Client;
using DaServer.Rpc;
using DaServer.Runtime;

namespace DaServer.Benchmark;

/// <summary>
/// The realm broadcast of the 10k-player scenario (samples: every 5 ticks to every session): a game node and three
/// gateways, <see cref="Clients"/> real client sockets spread over the gateways. One operation = one broadcast to
/// <see cref="ClientGroup.All"/> from the game node, received by every client (serialized once, one frame per gateway,
/// one copy per session). Allocations include the clients' receive path. The full scenario with 10k bots in separate
/// processes is Sample.Bot; 9k is the most one process can hold here (both socket ends, 20k descriptors).
/// </summary>
[MemoryDiagnoser]
public class ScenarioBenchmark
{
    private sealed class Receiver(Countdown countdown) : IRoomClient
    {
        public void OnTick(int sequence) => countdown.Signal();
    }

    private readonly Countdown _countdown = new();
    private Node[] _nodes = null!;
    private ClusterClient[] _clients = null!;
    private RoomClientProxy _everyone;
    private int _sequence;

    [Params(3_000, 9_000)] public int Clients;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _nodes = await Clusters.Start(4, shards: 4, gateways: 3);
        var receiver = new Receiver(_countdown);
        _clients = new ClusterClient[Clients];
        for (int i = 0; i < Clients; i++)
        {
            var client = _clients[i] = new ClusterClient();
            await client.ConnectAsync("127.0.0.1", _nodes[i % 3].ClientPort, followDispatch: false);
            client.Register<IRoomClient>(receiver);
        }

        _everyone = _nodes[3].RoomClient(ClientGroup.All); // sent from the game node, like the realm system
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var client in _clients) client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Clusters.Stop(_nodes).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task BroadcastToEveryone()
    {
        var delivered = _countdown.Reset(Clients);
        _everyone.OnTick(++_sequence);
        return delivered;
    }
}
