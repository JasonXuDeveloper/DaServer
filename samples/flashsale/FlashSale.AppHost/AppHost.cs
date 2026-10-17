// The flash sale: two sale nodes taking clients (TCP, WebSocket at /clients/ws), their records in PostgreSQL, and the
// crowd.
//   dotnet run --project samples/flashsale/FlashSale.AppHost                 (1,000,000 buyers for 100 units)
//   dotnet run --project samples/flashsale/FlashSale.AppHost -- Buyers=10000
// Published as Docker Compose files (default) or a Helm chart for Kubernetes:
//   aspire publish --project samples/flashsale/FlashSale.AppHost [-- Publish=k8s]
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
bool kubernetes = builder.AddPublishEnvironment();

var cluster = builder.AddDaServerCluster("flashsale");
var actors = builder.AddPostgres("postgres").WithDataVolume().AddDatabase("actors");
var sale = cluster.AddNodes<Projects.FlashSale_Server>("sale", replicas: 2).WithActorStore(actors).WithClientConnections();

// On Kubernetes, a sale's crowd comes in waves: the platform adds sale nodes as their CPU climbs (new nodes join and take
// their share of the stock shards and sessions) and removes them after (they leave gracefully); a vertical autoscaler
// only recommends the nodes' requests, it never restarts them during a sale.
if (kubernetes)
    sale.Nodes[0].PublishAsKubernetesService(node =>
    {
        node.AdditionalResources.Add(Platform.ScaleOnCpu(node, min: 2, max: 10, percent: 70));
        node.AdditionalResources.Add(Publishing.VerticalAutoscaler(node, "Off")); // recommendations only
    });

// Crowd options: `dotnet run --project samples/flashsale/FlashSale.Crowd -- --help`.
builder.AddProject<Projects.FlashSale_Crowd>("crowd")
    .WithClientEndpoints(sale)
    .WithArgs("--buyers", builder.Configuration.GetValue("Buyers", 1_000_000).ToString(),
        "--units", builder.Configuration.GetValue("Units", 100).ToString());

builder.Build().Run();
