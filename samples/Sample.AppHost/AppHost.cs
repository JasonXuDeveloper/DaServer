// The whole sample cluster, with the Aspire dashboard (logs, traces, metrics):
//   dotnet run --project samples/Sample.AppHost
// Publish the same model as Docker Compose files:
//   aspire publish --project samples/Sample.AppHost

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose");

// A node hosts what its project compiles: the topology is projects and replica counts. Ports, names and seeds are
// assigned here; discovery (gossip) and client placement (dispatch) are automatic.
var cluster = builder.AddDaServerCluster("sample");
cluster.AddNodes<Projects.Sample_Login>("login");
cluster.AddNodes<Projects.Sample_Game>("game");
cluster.AddGateways<Projects.Sample_Gateway>("gateway", replicas: 3);
var dispatch = cluster.AddDispatch<Projects.Sample_Gateway>("dispatch");

builder.AddProject<Projects.Sample_Bot>("bots")
    .WithGateways(dispatch)
    // 10k players chatting in 100 maps while the realm broadcasts to everyone every 5 ticks. Moves per second (4th
    // argument, 0 here) adds per-tick position sync: on one machine the bots, not the servers, saturate first.
    .WithArgs("10000", "60", "100", "0", "10");

builder.Build().Run();
