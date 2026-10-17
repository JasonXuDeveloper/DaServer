# Flash sale sample

A million buyers for 100 units. Buyers talk to the sale over the client protocol (TCP, or WebSocket at `/clients/ws`
for browsers), never over the HTTP plane, which is for low-frequency traffic (ADR-046). A stateless `Gate` service
turns their messages into asks of the sale's actors:

- a waiting room per sale admits buyers at the pace the sale can take (`AdmissionSystem`, every tick) and, once every
  stock shard is empty, turns newcomers away with one answer each;
- the stock is split into 16 `StockShard` actors: a turn is the atomic decrement, no lock, no Redis;
- each buyer's order is an `Order` actor keyed (user, sale): a retry waits for the running purchase and gets the
  same answer.

| Project | What |
|---|---|
| `FlashSale.Contracts` | ids (`StockShardId`, `OrderId`, `WaitingRoomId`) and messages; `[FromClient]` ones for the gate |
| `FlashSale.Components` | `[Persist]` stock, purchase and waiting-room state |
| `FlashSale.Server` | the node: `StockShard`, `Order`, `WaitingRoom` actors, `AdmissionSystem`, the `Gate` service |
| `FlashSale.Crowd` | the buyers as a `DaServer.Simulation` swarm; checks exactly the stock sold, nothing left, retries answered alike, orders readable (`--web-socket` for the browser path) |
| `FlashSale.AppHost` | two sale nodes taking clients, PostgreSQL and the crowd (`dotnet run --project samples/flashsale/FlashSale.AppHost`) |
| `FlashSale.Tests` | the guarantees in an in-process cluster (`DaServer.Testing`), the waiting room on a manual clock |

One 4-core VM, two sale nodes and the crowd on it, 1,000,000 buyers for 100 units: 6.9 s, 1,035,429 requests (150,000
a second, 262,000 at peak), about 79,000 per node core; exactly 100 sold, no retry answered differently. Over
WebSocket: 8.4 s.
