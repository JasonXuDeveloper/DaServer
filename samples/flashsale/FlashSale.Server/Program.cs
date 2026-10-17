// A node of the flash sale: stock shards, orders and waiting rooms, and the sale's HTTP API (Endpoints.cs). Options come
// from configuration (DaServer__* environment variables, which the AppHost sets).
using DaServer.Hosting;
using FlashSale.Server;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
builder.AddDaServer();
builder.AddPostgreSqlActorStore(); // stock, orders and queues ([Persist]); the AppHost passes the "actors" database
var app = builder.Build();
app.MapDaServer();
app.MapFlashSale();
app.Run();
