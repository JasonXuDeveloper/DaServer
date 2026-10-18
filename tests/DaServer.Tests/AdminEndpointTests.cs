using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DaServer.Tests;

/// <summary>The admin endpoints (A5): authorized by the token the signing secret derives, absent without a secret.</summary>
public class AdminEndpointTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    [Test]
    public async Task OnlyTheSecretsTokenIsAnswered()
    {
        await using var app = await StartNode(Secret);
        using var http = Client(app, token: null);
        Assert.That((await http.GetAsync("/admin/stats")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "no token");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken.For("another secret"));
        Assert.That((await http.GetAsync("/admin/stats")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), "another cluster's token");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken.For(Secret));
        Assert.That((await http.GetAsync("/admin/stats")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task WithoutASecretThereAreNoAdminEndpoints()
    {
        await using var app = await StartNode(secret: null);
        using var http = Client(app, AdminToken.For(Secret));
        Assert.That((await http.GetAsync("/admin/stats")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task MembershipAndStatsDescribeTheNode()
    {
        await using var app = await StartNode(Secret);
        using var http = Client(app, AdminToken.For(Secret));
        var node = app.Services.GetRequiredService<Node>();
        await Task.Run(async () => { while (!node.IsReady) await Task.Delay(10); });
        await node.Ask(new CounterId(1), new Add(5));

        using var membership = JsonDocument.Parse(await http.GetStringAsync("/admin/membership"));
        var self = membership.RootElement.GetProperty("members")[0];
        Assert.That((self.GetProperty("name").GetString(), self.GetProperty("status").GetString()), Is.EqualTo(("admin", "Up")));
        Assert.That(membership.RootElement.GetProperty("coordinator").GetString(), Is.EqualTo("admin"));
        using var stats = JsonDocument.Parse(await http.GetStringAsync("/admin/stats"));
        Assert.That(stats.RootElement.GetProperty("actors").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public async Task UnloadingAnActorTypeUnloadsItsActors()
    {
        await using var app = await StartNode(Secret);
        using var http = Client(app, AdminToken.For(Secret));
        var node = app.Services.GetRequiredService<Node>();
        Assert.That(await node.Ask(new CounterId(1), new Add(5)), Is.EqualTo(5));
        Assert.That((await http.PostAsync("/admin/unload/NoSuchActor", null)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await http.PostAsync("/admin/unload/Counter", null)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await Task.Run(async () => { while (node.ActorCount > 0) await Task.Delay(10); }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(await node.Ask(new CounterId(1), new Get()), Is.Zero, "loaded again, without a store: from scratch");
    }

    [Test]
    public async Task DrainingStopsTheNodeGracefully()
    {
        await using var app = await StartNode(Secret);
        using var http = Client(app, AdminToken.For(Secret));
        var stopping = new TaskCompletionSource();
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(stopping.SetResult);
        Assert.That((await http.PostAsync("/admin/drain", null)).StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<WebApplication> StartNode(string? secret)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DaServer:Name"] = "admin",
            ["DaServer:ClusterName"] = "admin-" + Guid.NewGuid().ToString("N"),
            ["DaServer:ListenAddress"] = "127.0.0.1",
            ["DaServer:ClusterPort"] = "0",
            ["DaServer:HttpPort"] = "0",
            ["DaServer:Shards"] = "2",
            ["DaServer:StatsInterval"] = "0",
            ["DaServer:SigningSecret"] = secret,
        });
        builder.AddDaServer();
        var app = builder.Build();
        app.MapDaServer();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, string? token)
    {
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        if (token != null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}
