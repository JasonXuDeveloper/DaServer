// The flash sale: two sale nodes behind any HTTP load balancer, their records in PostgreSQL, and the crowd.
//   dotnet run --project samples/flashsale/FlashSale.AppHost                 (1,000,000 buyers for 100 units)
//   dotnet run --project samples/flashsale/FlashSale.AppHost -- Users=10000
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

var cluster = builder.AddDaServerCluster("flashsale");
var actors = builder.AddPostgres("postgres").WithDataVolume().AddDatabase("actors");
var sale = cluster.AddNodes<Projects.FlashSale_Server>("sale", replicas: 2).WithActorStore(actors);

// The crowd talks to every sale node over HTTP (any node serves any request) once the cluster is up.
var load = builder.AddProject<Projects.FlashSale_Load>("crowd")
    .WithArgs("--Sale:Users", builder.Configuration.GetValue("Users", 1_000_000).ToString(),
        "--Sale:Units", builder.Configuration.GetValue("Units", 100).ToString());
for (int i = 0; i < sale.Nodes.Count; i++)
{
    var node = sale.Nodes[i];
    load.WaitFor(node).WithEnvironment($"Sale__Urls__{i}", node.GetEndpoint("http"));
}

builder.Build().Run();
