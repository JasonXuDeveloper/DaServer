using System.Net;
using System.Runtime.CompilerServices;
using DaServer.Benchmark.Kestrel;
using DaServer.Client;
using DaServer.Clients;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DaServer.Benchmark;

/// <summary>
/// Client transports compared (roadmap: "Evaluate Kestrel's raw TCP"): the gateway's own listener and sockets against
/// Kestrel (<c>ListenOptions.Run</c>, a connection's pipes) feeding the same client connections. One broadcast to every
/// client, and 64 clients each making a request through the gateway (a service that asks an actor) at once.
/// </summary>
[MemoryDiagnoser, ThreadingDiagnoser]
public class TransportBenchmark
{
    private readonly Countdown _countdown = new();
    private Node[] _nodes = null!;
    private ClusterClient[] _clients = null!;
    private WebApplication? _kestrel;
    private RoomId _room;
    private int _sequence;

    public enum Kind { Socket, Kestrel }

    [Params(Kind.Socket, Kind.Kestrel)] public Kind Transport;

    [Params(1_000, 5_000)] public int Clients;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _nodes = await Clusters.Start(2, shards: 4, gateways: 1);
        _room = Clusters.IdOwnedBy<RoomId>(_nodes[0], _nodes[1]);
        int port = Transport == Kind.Socket ? _nodes[0].ClientPort() : await StartKestrel(_nodes[0].Feature<ClientConnections>()!);
        _clients = new ClusterClient[Clients];
        for (int i = 0; i < Clients; i++)
        {
            var client = _clients[i] = new ClusterClient();
            await client.ConnectAsync("127.0.0.1", port);
            client.Register(new Receiver(_countdown));
        }

        foreach (var client in _clients) await client.Ask(new Join(_room, 1));
    }

    /// <summary>Kestrel on a loopback port of its own, each connection a <see cref="PipeChannel"/> of the gateway's client connections.</summary>
    private async Task<int> StartKestrel(ClientConnections connections)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Run(async connection =>
        {
            var channel = new PipeChannel(connection.Transport, connection.Abort, connections.Channels, connections.MaxFrameSize,
                connections.MaxPendingSendBytes);
            connections.Open(channel);
            await channel.RunAsync();
        })));
        _kestrel = builder.Build();
        await _kestrel.StartAsync();
        var address = _kestrel.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address).Port;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Clusters.Stop(_nodes, _clients);
        _kestrel?.StopAsync().GetAwaiter().GetResult();
    }

    [Benchmark, AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask Broadcast()
    {
        var delivered = _countdown.Reset(Clients);
        await _nodes[1].Ask(_room, new Broadcast(1, ++_sequence));
        await delivered;
    }

    private readonly Task<bool>[] _calls = new Task<bool>[64];

    [Benchmark(OperationsPerInvoke = 64)]
    public void ClientRequests()
    {
        for (int i = 0; i < _calls.Length; i++) _calls[i] = _clients[i].Ask(new Join(_room, 0)).AsTask();
        Task.WaitAll(_calls);
    }
}
