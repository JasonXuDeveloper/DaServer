// The whole sample cluster, with the Aspire dashboard (logs, traces, metrics):
//   dotnet run --project samples/Sample.AppHost
// Publish the same model as Docker Compose files:
//   aspire publish --project samples/Sample.AppHost

using System;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

// A node hosts what its project compiles: the topology is projects and replica counts. Ports, names and seeds are
// assigned here; discovery (gossip) and client placement (balancer) are automatic.
var cluster = builder.AddDaServerCluster("sample");
cluster.AddNodes<Projects.Sample_Login>("login");
cluster.AddNodes<Projects.Sample_Game>("game");
cluster.AddGateways<Projects.Sample_Gateway>("gateway", replicas: 3);
var balancer = cluster.AddBalancer<Projects.Sample_Gateway>("balancer");

// 10k players moving in 200 maps (50 per map, every position change sent to the whole map each tick) while the realm
// broadcasts to everyone every 5 ticks: about 10 million position updates a second reach the bots, no strings involved
// (Bots and Seconds can be overridden: `dotnet run -- Bots=1000`, the e2e test does). Bot arguments: bots, seconds,
// maps, moves per second, chat every n seconds (0 = off).
int bots = builder.Configuration.GetValue("Bots", 10_000);
int seconds = builder.Configuration.GetValue("Seconds", 60);
builder.AddProject<Projects.Sample_Bot>("bots")
    .WithGateways(balancer)
    .WithArgs(bots.ToString(), seconds.ToString(), Math.Max(1, bots / 50).ToString(), "1", "0");

builder.Build().Run();
