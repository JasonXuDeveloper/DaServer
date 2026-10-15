using System.Net;
using System.Net.Http.Json;
using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DaServer:Name"] = "http",
            ["DaServer:ListenAddress"] = "127.0.0.1",
            ["DaServer:ClusterPort"] = "0",
            ["DaServer:HttpPort"] = "0",
            ["DaServer:Shards"] = "2",
            ["DaServer:StatsInterval"] = "0",
        });
        builder.AddDaServer();
        builder.Services.Configure<NodeOptions>(static o => o.Features.Add(new LimitWhoAsks()));
        _app = builder.Build();
        _app.MapAsk<EchoText, string>("/echo");
        _app.MapAsk<CounterId, Add, int>("/counters/{id}/add");
        _app.MapAsk<WhoAsks, long>("/who");
        await _app.StartAsync();
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

    /// <summary>An ingress filter: <see cref="WhoAsks"/> is rate limited for three seconds.</summary>
    private sealed class LimitWhoAsks : DaServer.Features.NodeFeature, IIngressFilter
    {
        protected internal override void Start(DaServer.Features.INodeContext node) => node.AddIngressFilter(this);

        public IngressDecision Check(uint messageId, uint target, long key, Origin origin) =>
            messageId == MessageType<WhoAsks>.Id ? IngressDecision.Refuse(RpcStatus.RateLimited, TimeSpan.FromSeconds(3)) : IngressDecision.Admit;
    }
}
