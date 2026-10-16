// The whole sample cluster, with the Aspire dashboard (logs, traces, metrics):
//   dotnet run --project samples/mmo/Sample.AppHost
// Publish the same model as Docker Compose files:
//   aspire publish --project samples/mmo/Sample.AppHost

using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
// Where `aspire publish` writes the cluster: Docker Compose (default) or Kubernetes (`-- Publish=k8s`, a Helm chart).
if (builder.Configuration["Publish"] == "k8s") builder.AddKubernetesEnvironment("k8s");
else builder.AddDockerComposeEnvironment("compose");

// One project per server: a node hosts exactly the actors, services and systems its project compiles (its startup log
// lists them). The topology is projects and replica counts; ports, names and seeds are assigned here, discovery
// (gossip) and client placement (the gateways' directory) are automatic. Who talks to whom: samples/mmo/README.md.
var cluster = builder.AddDaServerCluster("sample");
// Actor records ([Persist] components, here player profiles) live in PostgreSQL; the volume keeps them across runs.
var actors = builder.AddPostgres("postgres").WithDataVolume().AddDatabase("actors");
cluster.AddNodes<Projects.Sample_LoginServer>("login");                    // Account service
cluster.AddNodes<Projects.Sample_GameServer>("game").WithActorStore(actors); // Player and Realm actors
cluster.AddNodes<Projects.Sample_MapServer>("map");                        // Map actors + movement/interest systems
var gateways = cluster.AddNodes<Projects.Sample_GatewayServer>("gateway", replicas: 3).WithClientConnections(); // clients, no business code

// 10k players moving in one map on one map node while the realm broadcasts to everyone every 5 ticks. Each player sees
// the 3x3 cells around it (area of interest) and hears about avatars only when they turn (dead reckoning): about
// 2.5 million position updates a second reach the bots, no strings involved. Bots, Seconds, Maps, TravelEvery (s
// between a bot's map changes, 0 = never) and Lean (bots keep no view of others; the default, since 10k clients share
// the machine with the servers) can be overridden (`dotnet run -- Bots=1000 Lean=false`; the e2e test changes maps).
// Bot options: `dotnet run --project samples/mmo/Sample.Bot -- --help`.
int bots = builder.Configuration.GetValue("Bots", 10_000);
int seconds = builder.Configuration.GetValue("Seconds", 60);
int maps = builder.Configuration.GetValue("Maps", 1);
int travelEvery = builder.Configuration.GetValue("TravelEvery", 0);
bool lean = builder.Configuration.GetValue("Lean", true); // 10k clients and the servers share this machine
builder.AddProject<Projects.Sample_Bot>("bots")
    .WithClientEndpoints(gateways)
    .WithArgs("--bots", bots.ToString(), "--seconds", seconds.ToString(), "--maps", maps.ToString(),
        "--turns-per-second", "1", "--chat-every", "0", "--travel-every", travelEvery.ToString())
    .WithArgs(context => { if (lean) context.Args.Add("--lean"); });

builder.Build().Run();
