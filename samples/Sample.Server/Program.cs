using DaServer.Hosting;
using Microsoft.Extensions.Hosting;

// Roles, ports and seeds come from configuration (appsettings.json, DaServer__* environment variables, command line),
// so the same binary runs as a gateway, a game node or both.
var builder = Host.CreateApplicationBuilder(args);
builder.AddDaServer();
await builder.Build().RunAsync();
