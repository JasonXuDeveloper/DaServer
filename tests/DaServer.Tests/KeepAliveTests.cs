using System.Net;
using System.Net.Sockets;

namespace DaServer.Tests;

/// <summary>Keep-alive (ADR-053): idle connections stay up, silent ones are closed on both sides within their timeout.</summary>
public class KeepAliveTests : ClusterTest
{
    [Test]
    public async Task ANodeClosesClientsThatGoSilent()
    {
        var gateway = await Start("gw", gateway: true, clients: static o => o.ClientTimeout = TimeSpan.FromMilliseconds(300));
        using var silent = new TcpClient(); // connects, then sends nothing: a vanished client
        await silent.ConnectAsync(IPAddress.Loopback, gateway.ClientPort());
        await Eventually(() => gateway.SessionCount() == 1, "the session did not open");
        await Eventually(() => gateway.SessionCount() == 0, "a silent client was kept");
    }

    [Test]
    public async Task IdleClientsPingAndStayConnected()
    {
        var gateway = await Start("gw", gateway: true, clients: static o => o.ClientTimeout = TimeSpan.FromMilliseconds(600));
        var client = Track(new ClusterClient
        {
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
            ServerTimeout = TimeSpan.FromMilliseconds(600),
        });
        int disconnects = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnects);
        await client.ConnectAsync("127.0.0.1", gateway.ClientPort());

        await Task.Delay(2000); // several timeouts without a message either way
        Assert.That(client.IsConnected, Is.True);
        Assert.That(disconnects, Is.Zero);
        Assert.That(gateway.SessionCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task ClientsNoticeASilentNodeAndProbesNoticeItSooner()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); // accepts, never reads or answers
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new List<Socket>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true) accepted.Add(await listener.AcceptSocketAsync());
            }
            catch (Exception)
            {
                // stopped
            }
        });

        var timedOut = await DisconnectAfter(new ClusterClient { ServerTimeout = TimeSpan.FromMilliseconds(400) }, port, null);
        Assert.That(timedOut, Is.LessThan(TimeSpan.FromSeconds(2)), "silence beyond ServerTimeout closes the connection");

        var probed = await DisconnectAfter(new ClusterClient { ServerTimeout = TimeSpan.FromMinutes(1) }, port, TimeSpan.FromMilliseconds(300));
        Assert.That(probed, Is.LessThan(TimeSpan.FromSeconds(2)), "an unanswered probe closes it without waiting for ServerTimeout");

        listener.Stop();
        foreach (var socket in accepted) socket.Dispose();
    }

    private async Task<TimeSpan> DisconnectAfter(ClusterClient client, int port, TimeSpan? probe)
    {
        Track(client);
        client.AutoReconnect = false;
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += e => lost.TrySetResult();
        await client.ConnectAsync("127.0.0.1", port, followRedirect: false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (probe is { } timeout) client.CheckConnection(timeout);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return watch.Elapsed;
    }

    [Test]
    public async Task IdleNodeLinksPingInsteadOfReconnecting()
    {
        static void Fast(NodeOptions o) => (o.GossipInterval, o.SuspectAfter) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));
        var a = await Start("a", configure: Fast);
        var b = await Start("b", configure: Fast);
        await Converged();
        var link = a.LinkTo(b.Id)!;
        await Eventually(() => link.Connection != null && link.Inbound != null, "the link did not connect both ways");
        var (outgoing, inbound) = (link.Connection, link.Inbound);

        await Task.Delay(1500); // five timeouts with no message on the link
        Assert.That(link.Connection, Is.SameAs(outgoing), "the idle outgoing connection was kept alive by pings");
        Assert.That(link.Inbound, Is.SameAs(inbound), "the idle incoming connection was kept alive by pings");
    }
}
