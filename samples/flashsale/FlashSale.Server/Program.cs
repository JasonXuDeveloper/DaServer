// A node of the flash sale: stock shards, orders, waiting rooms and the gate buyers talk to over the client protocol
// (TCP, or WebSocket at /clients/ws). Options come from configuration (DaServer__* environment variables, which the
// AppHost sets).
using DaServer.Hosting;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.AddDaServer();
builder.AddPostgreSqlActorStore(); // stock, orders and queues ([Persist]); the AppHost passes the "actors" database
var app = builder.Build();
app.MapDaServer();
app.Run();
