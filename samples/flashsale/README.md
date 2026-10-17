# Flash sale sample

A million buyers for 100 units, on DaServer: a waiting room admits buyers at the pace the sale can take, the stock is
split into 16 shards (each an actor, a turn is the atomic decrement: no lock, no Redis), each buyer's order is an
actor (a retry waits for the running purchase and gets the same answer), and once every shard is empty the room turns
newcomers away and each node remembers it, so the late crowd costs one HTTP answer and no cluster traffic.

| Project | What |
|---|---|
| `FlashSale.Contracts` | ids (`StockShardId`, `OrderId`, `WaitingRoomId`) and messages |
| `FlashSale.Components` | `[Persist]` stock, purchase and waiting-room state |
| `FlashSale.Server` | the node: `StockShard`, `Order`, `WaitingRoom` actors, `AdmissionSystem`, the HTTP API with signed tickets and passes (`Endpoints.cs`) |
| `FlashSale.Load` | the crowd over HTTP; checks exactly the stock sold, nothing left, retries answered alike, orders readable |
| `FlashSale.AppHost` | two sale nodes, PostgreSQL and the crowd (`dotnet run --project samples/flashsale/FlashSale.AppHost`) |
| `FlashSale.Tests` | the guarantees in an in-process cluster (`DaServer.Testing`), the waiting room on a manual clock |

One 4-core VM, two sale nodes and the crowd on it: 1,000,000 buyers, 100 units: done in 23.6 s, 1,003,102 requests
(42,500 a second, p50 under 66 ms, p99 under 262 ms with 2,000 connections), 100 confirmed, 0 left, no retry
answered differently. The nodes used 2.2 cores (about 19,500 requests a second per core), the crowd the rest.
