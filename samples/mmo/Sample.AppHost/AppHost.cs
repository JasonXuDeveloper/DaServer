// The whole sample cluster, with the Aspire dashboard (logs, traces, metrics):
//   dotnet run --project samples/mmo/Sample.AppHost
// Publish the same model as Docker Compose files, or as a Helm chart for Kubernetes:
//   aspire publish --project samples/mmo/Sample.AppHost
//   aspire publish --project samples/mmo/Sample.AppHost -- Publish=k8s

using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
// Where `aspire publish` writes the cluster: Docker Compose (default) or Kubernetes (`-- Publish=k8s`, a Helm chart).
bool kubernetes = builder.Configuration["Publish"] == "k8s";
if (kubernetes) builder.AddKubernetesEnvironment("k8s");
else builder.AddDockerComposeEnvironment("compose");

// One project per server: a node hosts exactly the actors, services and systems its project compiles (its startup log
// lists them). The topology is projects and replica counts; ports, names and seeds are assigned here, discovery
// (gossip) and client placement (the gateways' directory) are automatic. Who talks to whom: samples/mmo/README.md.
var cluster = builder.AddDaServerCluster("sample");
// Actor records ([Persist] components, here player profiles) live in PostgreSQL. The database outlives the AppHost
// (persistent lifetime, a data volume): stopping the AppHost stops every resource at once, and the nodes still save
// their actors on the way out; the next run reuses the running container.
var actors = builder.AddPostgres("postgres").WithLifetime(ContainerLifetime.Persistent).WithDataVolume().AddDatabase("actors");
cluster.AddNodes<Projects.Sample_LoginServer>("login");                    // Account service
var game = cluster.AddNodes<Projects.Sample_GameServer>("game").WithActorStore(actors); // Player and Realm actors
cluster.AddNodes<Projects.Sample_MapServer>("map");                        // Map actors + movement/interest systems
var gateways = cluster.AddNodes<Projects.Sample_GatewayServer>("gateway", replicas: 3).WithClientConnections(); // clients, no business code
// Pushes to a client leave on a 2 ms beat: a session's positions and broadcasts within it share one write (ADR-070).
foreach (var gateway in gateways.Nodes) gateway.WithEnvironment("DaServer__Clients__FlushInterval", "00:00:00.002");

// On Kubernetes, what this game asks of the platform (the framework adds none of it): players reach the gateways
// through a load balancer, and a drain of the machines takes one game node at a time (its players' actors move to the
// others, saved and loaded, ADR-072).
if (kubernetes)
{
    gateways.Nodes[0].PublishAsKubernetesService(gateway => gateway.AdditionalResources.Add(Platform.LoadBalancer(gateway, "client")));
    game.Nodes[0].PublishAsKubernetesService(node => node.AdditionalResources.Add(Platform.OneAtATime(node)));
}

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
