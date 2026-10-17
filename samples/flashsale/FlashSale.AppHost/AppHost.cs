// The flash sale: two sale nodes taking clients (TCP, WebSocket at /clients/ws), their records in PostgreSQL, and the
// crowd.
//   dotnet run --project samples/flashsale/FlashSale.AppHost                 (1,000,000 buyers for 100 units)
//   dotnet run --project samples/flashsale/FlashSale.AppHost -- Buyers=10000
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

var cluster = builder.AddDaServerCluster("flashsale");
var actors = builder.AddPostgres("postgres").WithDataVolume().AddDatabase("actors");
var sale = cluster.AddNodes<Projects.FlashSale_Server>("sale", replicas: 2).WithActorStore(actors).WithClientConnections();

// Crowd options: `dotnet run --project samples/flashsale/FlashSale.Crowd -- --help`.
builder.AddProject<Projects.FlashSale_Crowd>("crowd")
    .WithClientEndpoints(sale)
    .WithArgs("--buyers", builder.Configuration.GetValue("Buyers", 1_000_000).ToString(),
        "--units", builder.Configuration.GetValue("Units", 100).ToString());

builder.Build().Run();
