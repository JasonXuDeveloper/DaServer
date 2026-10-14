using DaServer.Hosting;
using Microsoft.Extensions.Hosting;

// Every node of the sample: what it hosts is what its project compiles; ports and seeds come from configuration
// (appsettings.json, DaServer__* environment variables set by the AppHost, command line).
var builder = Host.CreateApplicationBuilder(args);
builder.AddDaServer();
await builder.Build().RunAsync();
