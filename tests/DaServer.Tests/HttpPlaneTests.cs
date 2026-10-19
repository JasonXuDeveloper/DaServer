using System.Net;
using System.Net.Http.Json;
using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DaServer.Tests;

/// <summary>The HTTP plane (ADR-046): minimal API endpoints whose requests enter the cluster through the ingress.</summary>
public class HttpPlaneTests
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    [SetUp]
    public async Task StartNode()
    {
        _app = await HostedNodes.StartAsync("http", settings: new() { ["DaServer:Clients:Port"] = "0", ["DaServer:Probe:Level"] = "7" },
            configure: static builder =>
            {
                builder.AddDaServerFeature<ProbeOptions>("Probe", static o => new ProbeFeature(o));
                builder.AddDaServerFeature<ProbeOptions>("Unconfigured", static o => new UnconfiguredFeature());
                builder.Services.Configure<NodeOptions>(static o => o.Features.Add(new LimitWhoAsks()));
            },
            map: static app =>
            {
                app.MapAsk<EchoText, string>("/echo");
                app.MapAsk<CounterId, Add, int>("/counters/{id}/add");
                app.MapAsk<WhoAsks, long>("/who");
                app.MapSend<Tally>("/tally");
            });
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    [TearDown]
    public async Task StopNode()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]
    public async Task HttpClientsOfTheNodeResolveAspireReferences()
    {
        // AddDaServer is the service defaults: an Aspire reference (services__{name}__{endpoint}__0) names a service the
        // node's HttpClients reach by name.
        _app.Configuration["services:self:http:0"] = _app.Urls.First();
        var http = _app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        Assert.That((await http.GetAsync("http://self/alive")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task HealthSaysReadyOnceJoinedAndAliveWhileNoShardStalls()
    {
        var node = await _app.ReadyNode();
        Assert.That((await _http.GetAsync("/health")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "ready: joined and not leaving");
        Assert.That((await _http.GetAsync("/alive")).StatusCode, Is.EqualTo(HttpStatusCode.OK), "alive: no stalled shard");
        var host = _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value;
        Assert.That(host.ShutdownTimeout, Is.EqualTo(node.Options.ShutdownTimeout + DaServerHostingExtensions.ShutdownMargin),
            "the host waits longer than the node's stop budget");
    }

    [Test]
    public void FeaturesRunWhenTheirSectionIsConfiguredAndReportGauges()
    {
        var node = _app.Node();
        Assert.That(node.Feature<ProbeFeature>()?.Level, Is.EqualTo(7), "options bound from DaServer:Probe");
        Assert.That(node.Feature<UnconfiguredFeature>(), Is.Null, "no section, no feature");
        Assert.That(node.Gauges.Select(static g => g.Name), Is.SupersetOf(new[] { "daserver.clients.sessions", "test.probe.level" }));
        Assert.That(node.Gauges.Single(static g => g.Name == "test.probe.level").Read(), Is.EqualTo(7));
    }

    public sealed class ProbeOptions
    {
        public int Level { get; set; }
    }

    private sealed class ProbeFeature(ProbeOptions options) : NodeFeature
    {
        public int Level => options.Level;

        protected internal override void Start(INodeContext node) => node.AddGauge("test.probe.level", "Probe level", () => options.Level);
    }

    private sealed class UnconfiguredFeature : NodeFeature;

    [Test]
    public async Task MetricsAreScrapedFromTheHttpPlane()
    {
        // What an HPA reads through the Prometheus adapter or KEDA: the shards' busy ratio, without OTLP configured.
        string first = await _http.GetStringAsync("/metrics");
        Assert.That(first, Does.Contain("daserver_shard_busy_ratio").And.Contain("daserver_shard_tick_overruns"));
        Assert.That(first, Does.Contain("node=\""), "every measurement names its node");
    }

    [Test]
    public async Task EndpointsAskServicesAndActors()
    {
        var echo = await _http.PostAsJsonAsync("/echo", new { text = "hi" });
        Assert.That(await echo.Content.ReadFromJsonAsync<string>(), Is.EqualTo("hi"));

        await _http.PostAsJsonAsync("/counters/5/add", new { delta = 2 });
        var add = await _http.PostAsJsonAsync("/counters/5/add", new { delta = 3 });
        Assert.That(await add.Content.ReadFromJsonAsync<int>(), Is.EqualTo(5), "the same actor both times");
    }

    [Test]
    public async Task RefusalsMapToHttpStatusesWithRetryAfter()
    {
        var refused = await _http.PostAsJsonAsync("/who", new { });
        Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(refused.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public void TheNodesInstrumentsAreSampledWithItsName()
    {
        var node = _app.Node();
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = static (instrument, l) =>
        {
            if (instrument.Meter.Name == DaServer.Hosting.NodeMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        void Record(System.Diagnostics.Metrics.Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
                if (tag.Key == "node" && (string?)tag.Value == node.Options.Name) seen[instrument.Name] = value;
        }

        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        listener.Start();
        listener.RecordObservableInstruments();
        Assert.That(seen.Keys, Is.SupersetOf(new[] { "daserver.actors", "daserver.cluster.members", "daserver.clients.sessions", "test.probe.level" }));
        Assert.That(seen["test.probe.level"], Is.EqualTo(7), "a feature's gauge");
    }

    [Test]
    public async Task APostSendsAOneWayMessageToItsService()
    {
        long before = Interlocked.Read(ref Observed.Tallies);
        var response = await _http.PostAsJsonAsync("/tally", new { By = 3 });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await TestNodes.WaitUntilAsync(() => Interlocked.Read(ref Observed.Tallies) - before >= 3, TimeSpan.FromSeconds(5), "the tallies arrived");
        Assert.That(Interlocked.Read(ref Observed.Tallies) - before, Is.EqualTo(3));
    }

    [Test]
    public async Task ClientsConnectOverWebSockets()
    {
        var ws = new Uri(_app.Urls.First().Replace("http://", "ws://") + "/clients/ws");
        await using var client = new ClusterClient();
        var inbox = new GatewayTests.Inbox();
        client.Register(inbox);
        await client.ConnectAsync(ws);

        Assert.That(await client.Ask(new Login(new PlayerId(7))), Is.True);
        Assert.That(await client.Ask(new AddGold(4)), Is.EqualTo(4), "the session's player");
        await TestNodes.WaitUntilAsync(() => inbox.Gold == 4, TimeSpan.FromSeconds(2), "the push came back over the WebSocket");
    }

    /// <summary>An ingress filter: <see cref="WhoAsks"/> is rate limited for three seconds.</summary>
    private sealed class LimitWhoAsks : DaServer.Features.NodeFeature, IIngressFilter
    {
        protected internal override void Start(DaServer.Features.INodeContext node) => node.AddIngressFilter(this);

        public IngressDecision Admit(uint messageId, uint target, long key, Origin origin) =>
            messageId == TypeId<WhoAsks>.Value ? IngressDecision.Refuse(RpcStatus.RateLimited, TimeSpan.FromSeconds(3)) : IngressDecision.Admit;
    }
}
