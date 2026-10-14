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
// assigned here; discovery (gossip) and client placement (dispatch) are automatic.
var cluster = builder.AddDaServerCluster("sample");
cluster.AddNodes<Projects.Sample_Login>("login");
cluster.AddNodes<Projects.Sample_Game>("game");
cluster.AddGateways<Projects.Sample_Gateway>("gateway", replicas: 3);
var dispatch = cluster.AddDispatch<Projects.Sample_Gateway>("dispatch");

// 10k players chatting in 100 maps while the realm broadcasts to everyone every 5 ticks (Bots and Seconds can be
// overridden: `dotnet run -- Bots=1000`, the e2e test does). Moves per second (4th argument, 0 here) adds per-tick
// position sync: on one machine the bots, not the servers, saturate first.
int bots = builder.Configuration.GetValue("Bots", 10_000);
int seconds = builder.Configuration.GetValue("Seconds", 60);
builder.AddProject<Projects.Sample_Bot>("bots")
    .WithGateways(dispatch)
    .WithArgs(bots.ToString(), seconds.ToString(), Math.Max(1, bots / 100).ToString(), "0", "10");

builder.Build().Run();
