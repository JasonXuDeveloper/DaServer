// The whole sample cluster, with the Aspire dashboard (logs, traces, metrics):
//   dotnet run --project samples/Sample.AppHost
// Publish the same model as Docker Compose files:
//   aspire publish --project samples/Sample.AppHost

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

var cluster = builder.AddDaServerCluster("sample");
cluster.AddNodes<Projects.Sample_Game>("game", replicas: 2);
var gateways = cluster.AddGateways<Projects.Sample_Gateway>("gateway", replicas: 2);

builder.AddProject<Projects.Sample_Bot>("bots")
    .WithGateways(gateways)
    .WithArgs("200", "600", "10"); // bots, seconds, moves per second

builder.Build().Run();
