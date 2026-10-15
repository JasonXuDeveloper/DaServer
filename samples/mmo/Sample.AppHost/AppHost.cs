// The whole sample cluster, with the Aspire dashboard (logs, traces, metrics):
//   dotnet run --project samples/mmo/Sample.AppHost
// Publish the same model as Docker Compose files:
//   aspire publish --project samples/mmo/Sample.AppHost

using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

// One project per server: a node hosts exactly the actors, services and systems its project compiles (its startup log
// lists them). The topology is projects and replica counts; ports, names and seeds are assigned here, discovery
// (gossip) and client placement (the gateways' directory) are automatic. Who talks to whom: samples/mmo/README.md.
var cluster = builder.AddDaServerCluster("sample");
cluster.AddNodes<Projects.Sample_LoginServer>("login");                    // Account service
cluster.AddNodes<Projects.Sample_GameServer>("game");                      // Player and Realm actors
cluster.AddNodes<Projects.Sample_MapServer>("map");                        // Map actors + movement/interest systems
var gateways = cluster.AddNodes<Projects.Sample_Gateway>("gateway", replicas: 3).WithClients(); // clients, no business code

// 10k players moving in one map on one map node while the realm broadcasts to everyone every 5 ticks. Each player sees
// the 3x3 cells around it (area of interest) and hears about avatars only when they turn (dead reckoning): about
// 2.5 million position updates a second reach the bots, no strings involved. Bots, Seconds, Maps and TravelEvery (s
// between a bot's map changes, 0 = never) can be overridden (`dotnet run -- Bots=1000`; the e2e test changes maps).
// Bot arguments: bots, seconds, maps, turns per second, chat every n seconds (0 = off), max p99 ms, change map every n s.
int bots = builder.Configuration.GetValue("Bots", 10_000);
int seconds = builder.Configuration.GetValue("Seconds", 60);
int maps = builder.Configuration.GetValue("Maps", 1);
int travelEvery = builder.Configuration.GetValue("TravelEvery", 0);
builder.AddProject<Projects.Sample_Bot>("bots")
    .WithClientEndpoints(gateways)
    .WithArgs(bots.ToString(), seconds.ToString(), maps.ToString(), "1", "0", "250", travelEvery.ToString());

builder.Build().Run();
