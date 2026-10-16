// A node of the sample cluster: it hosts what this project compiles (Player and Realm actors). Options come from configuration
// (DaServer__* environment variables, which the AppHost sets).
using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.AddDaServer();
var app = builder.Build();
app.MapDaServer();
app.Run();
